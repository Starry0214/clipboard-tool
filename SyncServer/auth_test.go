package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func newTestApp(t *testing.T) (*app, *Store) {
	t.Helper()
	s, err := OpenStore("file::memory:?cache=shared")
	if err != nil {
		t.Fatalf("OpenStore: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	a := newApp()
	a.store = s
	return a, s
}

func doJSON(t *testing.T, a *app, method, path, body, token string) *httptest.ResponseRecorder {
	t.Helper()
	var req *http.Request
	if body == "" {
		req = httptest.NewRequest(method, path, nil)
	} else {
		req = httptest.NewRequest(method, path, strings.NewReader(body))
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	return rec
}

func TestRegisterAndLogin(t *testing.T) {
	a, s := newTestApp(t)
	rec := doJSON(t, a, "POST", "/api/auth/register", `{"username":"alice","password":"secret123","deviceName":"小米14 Pro"}`, "")
	if rec.Code != http.StatusCreated {
		t.Fatalf("register status = %d, body=%s", rec.Code, rec.Body.String())
	}
	var reg struct {
		DeviceID int64  `json:"deviceId"`
		Token    string `json:"token"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &reg); err != nil || reg.Token == "" || reg.DeviceID == 0 {
		t.Fatalf("register body = %s", rec.Body.String())
	}
	if _, err := s.GetDeviceByToken(sha256Hex(reg.Token)); err != nil {
		t.Fatalf("token not stored: %v", err)
	}

	// 重复用户名
	rec = doJSON(t, a, "POST", "/api/auth/register", `{"username":"alice","password":"secret123","deviceName":"pc"}`, "")
	if rec.Code != http.StatusConflict {
		t.Fatalf("duplicate register status = %d, want 409", rec.Code)
	}
	// 弱密码/短用户名
	if rec := doJSON(t, a, "POST", "/api/auth/register", `{"username":"ab","password":"x","deviceName":"pc"}`, ""); rec.Code != http.StatusBadRequest {
		t.Fatalf("short username status = %d, want 400", rec.Code)
	}
	// 登录成功（同账号第二设备）
	rec = doJSON(t, a, "POST", "/api/auth/login", `{"username":"alice","password":"secret123","deviceName":"PC 工作机"}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("login status = %d, body=%s", rec.Code, rec.Body.String())
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &reg); err != nil || reg.Token == "" {
		t.Fatalf("login body = %s", rec.Body.String())
	}
	// 密码错误
	rec = doJSON(t, a, "POST", "/api/auth/login", `{"username":"alice","password":"wrong","deviceName":"pc"}`, "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("bad password status = %d, want 401", rec.Code)
	}
	// 未注册用户
	rec = doJSON(t, a, "POST", "/api/auth/login", `{"username":"nobody","password":"x","deviceName":"pc"}`, "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("unknown user status = %d, want 401", rec.Code)
	}
}

func TestRequireAuth(t *testing.T) {
	a, _ := newTestApp(t)
	rec := doJSON(t, a, "POST", "/api/auth/register", `{"username":"alice","password":"secret123","deviceName":"phone"}`, "")
	var reg struct {
		Token string `json:"token"`
	}
	json.Unmarshal(rec.Body.Bytes(), &reg)

	// 无 token
	rec = doJSON(t, a, "GET", "/api/devices", "", "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("no token status = %d, want 401", rec.Code)
	}
	// 伪造 token
	rec = doJSON(t, a, "GET", "/api/devices", "", "deadbeef")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("bad token status = %d, want 401", rec.Code)
	}
	// 有效 token
	rec = doJSON(t, a, "GET", "/api/devices", "", reg.Token)
	if rec.Code != http.StatusOK {
		t.Fatalf("valid token status = %d, body=%s", rec.Code, rec.Body.String())
	}
}
