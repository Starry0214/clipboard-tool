package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestHistory(t *testing.T) {
	a, _ := newTestApp(t)
	did, tok := registerToken(t, a, "alice", "phone")
	uid, _ := a.store.GetDeviceByToken(sha256Hex(tok))
	_, _ = a.store.InsertMessage(uid.UserID, did, "clip_text", []byte(`{"text":"a"}`))
	_, _ = a.store.InsertMessage(uid.UserID, did, "clip_text", []byte(`{"text":"b"}`))

	req := httptest.NewRequest("GET", "/api/history?since=0", nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("history = %d %s", rec.Code, rec.Body.String())
	}
	var out struct {
		Messages []Message `json:"messages"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if len(out.Messages) != 2 || out.Messages[0].Type != "clip_text" {
		t.Fatalf("messages = %+v", out.Messages)
	}

	req = httptest.NewRequest("GET", "/api/history?since="+fmt.Sprint(out.Messages[1].Ts), nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec = httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil || len(out.Messages) != 0 {
		t.Fatalf("since filter = %+v, %v", out.Messages, err)
	}
}
