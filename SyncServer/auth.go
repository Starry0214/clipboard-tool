package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"

	"golang.org/x/crypto/bcrypt"
)

var contextKeyDevice = struct{}{}

func sha256Hex(s string) string {
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:])
}

func newToken() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

type authRequest struct {
	Username   string `json:"username"`
	Password   string `json:"password"`
	DeviceName string `json:"deviceName"`
}

type authResponse struct {
	DeviceID int64  `json:"deviceId"`
	Token    string `json:"token"`
}

func (a *app) handleRegister(w http.ResponseWriter, r *http.Request) {
	var req authRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json")
		return
	}
	if len(req.Username) < 4 || len(req.Password) < 6 {
		writeError(w, http.StatusBadRequest, "username >= 4 chars, password >= 6 chars")
		return
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(req.Password), bcrypt.DefaultCost)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	uid, err := a.store.CreateUser(req.Username, string(hash))
	if errors.Is(err, ErrUsernameTaken) {
		writeError(w, http.StatusConflict, "username taken")
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	writeAuthResponse(w, http.StatusCreated, a, uid, req.DeviceName)
}

func (a *app) handleLogin(w http.ResponseWriter, r *http.Request) {
	var req authRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json")
		return
	}
	u, err := a.store.GetUserByUsername(req.Username)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "bad credentials")
		return
	}
	if bcrypt.CompareHashAndPassword([]byte(u.PasswordHash), []byte(req.Password)) != nil {
		writeError(w, http.StatusUnauthorized, "bad credentials")
		return
	}
	writeAuthResponse(w, http.StatusOK, a, u.ID, req.DeviceName)
}

func writeAuthResponse(w http.ResponseWriter, status int, a *app, userID int64, deviceName string) {
	token, err := newToken()
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	if deviceName == "" {
		deviceName = "未命名设备"
	}
	did, err := a.store.CreateDevice(userID, deviceName, sha256Hex(token))
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(authResponse{DeviceID: did, Token: token})
}

func (a *app) requireAuth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		raw, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
		if !ok || raw == "" {
			writeError(w, http.StatusUnauthorized, "missing token")
			return
		}
		dev, err := a.store.GetDeviceByToken(sha256Hex(raw))
		if err != nil {
			writeError(w, http.StatusUnauthorized, "invalid token")
			return
		}
		a.store.TouchDevice(dev.ID)
		next(w, r.WithContext(withDevice(r.Context(), &dev)))
	}
}

func withDevice(ctx context.Context, d *Device) context.Context {
	return context.WithValue(ctx, contextKeyDevice, d)
}

func deviceFrom(r *http.Request) *Device {
	d, _ := r.Context().Value(contextKeyDevice).(*Device)
	return d
}

func (a *app) handleListDevices(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	list, err := a.store.ListDevices(dev.UserID)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	type devView struct {
		ID       int64  `json:"id"`
		Name     string `json:"name"`
		LastSeen int64  `json:"lastSeen"`
	}
	out := make([]devView, 0, len(list))
	for _, d := range list {
		out = append(out, devView{d.ID, d.Name, d.LastSeen})
	}
	json.NewEncoder(w).Encode(map[string]any{"devices": out})
}

func (a *app) handleDeleteDevice(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	var id int64
	if _, err := fmt.Sscanf(r.PathValue("id"), "%d", &id); err != nil {
		writeError(w, http.StatusBadRequest, "bad id")
		return
	}
	if err := a.store.DeleteDevice(dev.UserID, id); err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func writeError(w http.ResponseWriter, status int, msg string) {
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(map[string]string{"error": msg})
}
