package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

func main() {
	base := flag.String("base", "http://127.0.0.1:8082", "server base url")
	user := flag.String("user", "alice", "username")
	pass := flag.String("pass", "secret123", "password")
	device := flag.String("device", "phone-sim", "device name")
	kind := flag.String("kind", "text", "text | image | file")
	text := flag.String("text", "", "text content (kind=text)")
	media := flag.String("media", "", "media file path (kind=image|file)")
	flag.Parse()

	token := loginOrRegister(*base, *user, *pass, *device)
	fmt.Printf("token: %s\n", token)

	conn, _, err := websocket.DefaultDialer.Dial(
		strings.Replace(*base, "http", "ws", 1)+"/ws?token="+token, nil)
	if err != nil {
		fatal("ws dial", err)
	}
	defer conn.Close()

	switch *kind {
	case "text":
		send(conn, map[string]any{"type": "clip_text", "payload": map[string]string{"text": *text}})
		fmt.Printf("sent clip_text: %s\n", *text)
	case "image", "file":
		data, err := os.ReadFile(*media)
		if err != nil {
			fatal("read media", err)
		}
		mediaID := upload(*base, token, data)
		msgType := "clip_image"
		if *kind == "file" {
			msgType = "clip_file"
		}
		send(conn, map[string]any{"type": msgType, "payload": map[string]any{
			"mediaId": mediaID, "name": baseName(*media), "size": len(data),
		}})
		fmt.Printf("sent %s: %s (%d bytes, mediaId=%d)\n", msgType, baseName(*media), len(data), mediaID)
	}

	// 接收模式：打印 5 秒内收到的消息（验证电脑→手机方向）
	conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			break
		}
		fmt.Printf("received: %s\n", data)
	}
}

func loginOrRegister(base, user, pass, device string) string {
	body := fmt.Sprintf(`{"username":%q,"password":%q,"deviceName":%q}`, user, pass, device)
	resp, err := http.Post(base+"/api/auth/login", "application/json", strings.NewReader(body))
	if err != nil {
		fatal("login request", err)
	}
	if resp.StatusCode != http.StatusOK {
		resp.Body.Close()
		resp, err = http.Post(base+"/api/auth/register", "application/json", strings.NewReader(body))
		if err != nil {
			fatal("register request", err)
		}
	}
	defer resp.Body.Close()
	var out struct {
		Token string `json:"token"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	if out.Token == "" {
		fatal("auth failed", fmt.Errorf("status=%d", resp.StatusCode))
	}
	return out.Token
}

func upload(base, token string, data []byte) int64 {
	req, _ := http.NewRequest("POST", base+"/api/media", bytes.NewReader(data))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		fatal("upload", err)
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	var out struct {
		MediaID int64 `json:"mediaId"`
	}
	json.Unmarshal(b, &out)
	if out.MediaID == 0 {
		fmt.Printf("upload debug: url=%s auth=%s status=%d body=%s\n",
			req.URL, req.Header.Get("Authorization")[:20], resp.StatusCode, b)
		fatal("upload failed", fmt.Errorf("status=%d %s", resp.StatusCode, b))
	}
	return out.MediaID
}

func send(conn *websocket.Conn, msg any) {
	data, _ := json.Marshal(msg)
	if err := conn.WriteMessage(websocket.TextMessage, data); err != nil {
		fatal("ws send", err)
	}
}

func baseName(p string) string {
	if i := strings.LastIndexAny(p, `/\`); i >= 0 {
		return p[i+1:]
	}
	return p
}

func fatal(what string, err error) {
	fmt.Fprintf(os.Stderr, "phone-sim FAIL %s: %v\n", what, err)
	os.Exit(1)
}
