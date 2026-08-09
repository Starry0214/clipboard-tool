package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func registerToken(t *testing.T, a *app, username, deviceName string) (int64, string) {
	t.Helper()
	rec := doJSON(t, a, "POST", "/api/auth/register",
		`{"username":"`+username+`","password":"secret123","deviceName":"`+deviceName+`"}`, "")
	if rec.Code != http.StatusCreated {
		t.Fatalf("register = %d %s", rec.Code, rec.Body.String())
	}
	var reg struct {
		DeviceID int64  `json:"deviceId"`
		Token    string `json:"token"`
	}
	json.Unmarshal(rec.Body.Bytes(), &reg)
	return reg.DeviceID, reg.Token
}

func TestMediaUploadDownload(t *testing.T) {
	a, _ := newTestApp(t)
	_, tokA := registerToken(t, a, "alice", "phone")
	_, tokB := registerToken(t, a, "bobby", "phone")

	req := httptest.NewRequest("POST", "/api/media", strings.NewReader("PNGDATA123"))
	req.Header.Set("Authorization", "Bearer "+tokA)
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("upload = %d %s", rec.Code, rec.Body.String())
	}
	var up struct {
		MediaID int64 `json:"mediaId"`
	}
	json.Unmarshal(rec.Body.Bytes(), &up)
	if up.MediaID == 0 {
		t.Fatalf("upload body = %s", rec.Body.String())
	}

	req = httptest.NewRequest("GET", "/api/media/"+fmt.Sprint(up.MediaID), nil)
	req.Header.Set("Authorization", "Bearer "+tokA)
	rec = httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK || rec.Body.String() != "PNGDATA123" {
		t.Fatalf("download = %d %q", rec.Code, rec.Body.String())
	}

	// 跨账号 404
	req = httptest.NewRequest("GET", "/api/media/"+fmt.Sprint(up.MediaID), nil)
	req.Header.Set("Authorization", "Bearer "+tokB)
	rec = httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("cross-account = %d, want 404", rec.Code)
	}
}

func TestMediaTooLarge(t *testing.T) {
	a, _ := newTestApp(t)
	_, tok := registerToken(t, a, "alice", "phone")
	big := strings.Repeat("x", maxMediaBytes+1)
	req := httptest.NewRequest("POST", "/api/media", strings.NewReader(big))
	req.ContentLength = int64(len(big))
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("oversize = %d, want 413", rec.Code)
	}
}
