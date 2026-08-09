package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func smokeWrite(t *testing.T, c *websocket.Conn, msg string) {
	t.Helper()
	if err := c.WriteMessage(websocket.TextMessage, []byte(msg)); err != nil {
		t.Fatalf("write: %v", err)
	}
}

func smokeUpload(t *testing.T, base, token string, data []byte) int64 {
	t.Helper()
	req, _ := http.NewRequest("POST", base+"/api/media", bytes.NewReader(data))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("upload: %v", err)
	}
	defer resp.Body.Close()
	var out struct {
		MediaID int64 `json:"mediaId"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	if out.MediaID == 0 {
		t.Fatalf("upload status = %d", resp.StatusCode)
	}
	return out.MediaID
}

func smokeDownload(t *testing.T, base, token string, id int64) []byte {
	t.Helper()
	req, _ := http.NewRequest("GET", fmt.Sprintf("%s/api/media/%d", base, id), nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("download: %v", err)
	}
	defer resp.Body.Close()
	var buf bytes.Buffer
	buf.ReadFrom(resp.Body)
	return buf.Bytes()
}

func smokeHistory(t *testing.T, base, token string) []any {
	t.Helper()
	req, _ := http.NewRequest("GET", base+"/api/history?since=0", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("history: %v", err)
	}
	defer resp.Body.Close()
	var out struct {
		Messages []any `json:"messages"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	return out.Messages
}

func TestSmokeEndToEnd(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)

	_, tokA := registerToken(t, a, "alice", "phone")
	rec := doJSON(t, a, "POST", "/api/auth/login", `{"username":"alice","password":"secret123","deviceName":"pc"}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("login = %d %s", rec.Code, rec.Body.String())
	}
	var reg struct {
		Token string `json:"token"`
	}
	json.Unmarshal(rec.Body.Bytes(), &reg)
	tokB := reg.Token
	_, tokC := registerToken(t, a, "mallory", "phone")

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))
	connC := dialWS(t, wsURL(t, ts, tokC))
	defer connA.Close()
	defer connB.Close()
	defer connC.Close()

	smokeWrite(t, connA, `{"type":"clip_text","payload":{"text":"hello 世界"}}`)
	m := readMessage(t, connB)
	if m["type"] != "clip_text" || m["payload"].(map[string]any)["text"] != "hello 世界" {
		t.Fatalf("text forward = %v", m)
	}
	t.Log("PASS 文本互通")

	img := []byte("FAKEPNG-IMAGE-DATA-12345")
	mediaID := smokeUpload(t, ts.URL, tokA, img)
	smokeWrite(t, connA, fmt.Sprintf(`{"type":"clip_image","payload":{"mediaId":%d,"name":"a.png","size":%d}}`, mediaID, len(img)))
	m = readMessage(t, connB)
	if m["type"] != "clip_image" {
		t.Fatalf("image forward = %v", m)
	}
	got := smokeDownload(t, ts.URL, tokB, int64(m["payload"].(map[string]any)["mediaId"].(float64)))
	if !bytes.Equal(got, img) {
		t.Fatalf("image mismatch: got %d bytes want %d", len(got), len(img))
	}
	t.Log("PASS 图片上传/转发/下载")

	if hist := smokeHistory(t, ts.URL, tokB); len(hist) < 2 {
		t.Fatalf("history len = %d", len(hist))
	}
	t.Log("PASS 历史拉取")

	connC.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	smokeWrite(t, connA, `{"type":"clip_text","payload":{"text":"secret"}}`)
	if _, _, err := connC.ReadMessage(); err == nil {
		t.Fatal("cross-account leak")
	}
	t.Log("PASS 跨账号隔离")
}
