package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func wsURL(t *testing.T, ts *httptest.Server, token string) string {
	t.Helper()
	return "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws?token=" + token
}

func dialWS(t *testing.T, url string) *websocket.Conn {
	t.Helper()
	c, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { c.Close() })
	return c
}

func readMessage(t *testing.T, c *websocket.Conn) map[string]any {
	t.Helper()
	_, data, err := c.ReadMessage()
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	var m map[string]any
	if err := json.Unmarshal(data, &m); err != nil {
		t.Fatalf("unmarshal %s: %v", data, err)
	}
	return m
}

func TestWSForwardWithinAccount(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)

	didA, tokA := registerToken(t, a, "alice", "phone")
	rec := doJSON(t, a, "POST", "/api/auth/login", `{"username":"alice","password":"secret123","deviceName":"pc"}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("login = %d %s", rec.Code, rec.Body.String())
	}
	var reg struct {
		Token string `json:"token"`
	}
	json.Unmarshal(rec.Body.Bytes(), &reg)
	tokB := reg.Token

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))

	if err := connA.WriteMessage(websocket.TextMessage, []byte(`{"type":"clip_text","payload":{"text":"hello 世界"}}`)); err != nil {
		t.Fatalf("write A: %v", err)
	}
	m := readMessage(t, connB)
	if m["type"] != "clip_text" {
		t.Fatalf("B received type = %v", m)
	}
	if m["originDeviceId"] != float64(didA) {
		t.Fatalf("originDeviceId = %v, want %d", m["originDeviceId"], didA)
	}
	if m["payload"].(map[string]any)["text"] != "hello 世界" {
		t.Fatalf("payload = %v", m["payload"])
	}
	connA.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	if _, _, err := connA.ReadMessage(); err == nil {
		t.Fatal("source device should not receive its own message")
	}
}

func TestWSIsolationAcrossAccounts(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)

	_, tokA := registerToken(t, a, "alice", "phone")
	_, tokB := registerToken(t, a, "bobby", "phone")

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))

	_ = connA.WriteMessage(websocket.TextMessage, []byte(`{"type":"clip_text","payload":{"text":"secret"}}`))
	connB.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	if _, _, err := connB.ReadMessage(); err == nil {
		t.Fatal("cross-account message leaked")
	}
}

func TestWSRequiresToken(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)
	if _, _, err := websocket.DefaultDialer.Dial(wsURL(t, ts, "bad-token"), nil); err == nil {
		t.Fatal("dial with bad token should fail")
	}
}

// TestWSDeletePersistsToHistory：delete 消息须落库（带 seq），手动同步拉 history 时能拉到，
// 且广播给其他设备时携带 seq/ts。
func TestWSDeletePersistsToHistory(t *testing.T) {
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

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))

	// A 发一条文本，B 收到（带 seq）
	if err := connA.WriteMessage(websocket.TextMessage, []byte(`{"type":"clip_text","payload":{"text":"hello"}}`)); err != nil {
		t.Fatalf("write A: %v", err)
	}
	clipMsg := readMessage(t, connB)
	clipSeq, _ := clipMsg["seq"].(float64)

	// B 发 delete（同内容哈希），A 收到广播且带 seq
	hash := hashText("hello")
	if err := connB.WriteMessage(websocket.TextMessage,
		[]byte(`{"type":"delete","payload":{"hash":"`+hash+`"}}`)); err != nil {
		t.Fatalf("write B: %v", err)
	}
	delMsg := readMessage(t, connA)
	if delMsg["type"] != "delete" {
		t.Fatalf("A received type = %v", delMsg)
	}
	delSeq, _ := delMsg["seq"].(float64)
	if delSeq <= clipSeq {
		t.Fatalf("delete seq = %v, want > clip seq %v", delSeq, clipSeq)
	}
	if delMsg["payload"].(map[string]any)["hash"] != hash {
		t.Fatalf("delete payload = %v", delMsg["payload"])
	}

	// history 能拉到 delete 记录（type=delete，带 seq）
	hist := doJSON(t, a, "GET", "/api/history?since=0", "", tokA)
	if hist.Code != http.StatusOK {
		t.Fatalf("history = %d", hist.Code)
	}
	var body struct {
		Messages []Message `json:"messages"`
	}
	json.Unmarshal(hist.Body.Bytes(), &body)
	var found bool
	for _, m := range body.Messages {
		if m.Type == "delete" && m.Seq == int64(delSeq) {
			found = true
		}
	}
	if !found {
		t.Fatalf("delete record missing from history: %+v", body.Messages)
	}
}
