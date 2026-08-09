package main

import (
	"encoding/json"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	wsWriteWait  = 10 * time.Second
	wsPongWait   = 60 * time.Second
	wsPingPeriod = 30 * time.Second
)

var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
}

type Hub struct {
	mu    sync.Mutex
	conns map[int64]map[*conn]bool
}

type conn struct {
	userID   int64
	deviceID int64
	ws       *websocket.Conn
	send     chan []byte
}

func newHub() *Hub { return &Hub{conns: make(map[int64]map[*conn]bool)} }

func (h *Hub) register(c *conn) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.conns[c.userID] == nil {
		h.conns[c.userID] = make(map[*conn]bool)
	}
	h.conns[c.userID][c] = true
}

func (h *Hub) unregister(c *conn) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if set := h.conns[c.userID]; set != nil {
		delete(set, c)
		if len(set) == 0 {
			delete(h.conns, c.userID)
		}
	}
}

func (h *Hub) broadcast(userID int64, originDeviceID int64, msg []byte) {
	h.mu.Lock()
	defer h.mu.Unlock()
	for c := range h.conns[userID] {
		if c.deviceID == originDeviceID {
			continue
		}
		select {
		case c.send <- msg:
		default:
			close(c.send)
			delete(h.conns[userID], c)
		}
	}
}

type wsIncoming struct {
	Type    string          `json:"type"`
	Payload json.RawMessage `json:"payload"`
}

func (a *app) handleWS(w http.ResponseWriter, r *http.Request) {
	raw := r.URL.Query().Get("token")
	if raw == "" {
		writeError(w, http.StatusUnauthorized, "missing token")
		return
	}
	dev, err := a.store.GetDeviceByToken(sha256Hex(raw))
	if err != nil {
		writeError(w, http.StatusUnauthorized, "invalid token")
		return
	}
	ws, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	c := &conn{userID: dev.UserID, deviceID: dev.ID, ws: ws, send: make(chan []byte, 64)}
	a.hub.register(c)
	a.store.TouchDevice(dev.ID)
	go c.writeLoop()
	c.readLoop(a)
}

func (c *conn) readLoop(a *app) {
	defer func() {
		a.hub.unregister(c)
		c.ws.Close()
	}()
	c.ws.SetReadLimit(1 << 20)
	c.ws.SetReadDeadline(time.Now().Add(wsPongWait))
	c.ws.SetPongHandler(func(string) error {
		return c.ws.SetReadDeadline(time.Now().Add(wsPongWait))
	})
	for {
		_, data, err := c.ws.ReadMessage()
		if err != nil {
			return
		}
		var in wsIncoming
		if err := json.Unmarshal(data, &in); err != nil || in.Type == "" {
			continue
		}
		if in.Type == "delete" {
			var d struct {
				Hash string `json:"hash"`
			}
			if err := json.Unmarshal(in.Payload, &d); err != nil || d.Hash == "" {
				continue
			}
			if _, err := a.store.DeleteMessagesByHash(c.userID, d.Hash); err != nil {
				log.Printf("delete messages: %v", err)
				continue
			}
			out, _ := json.Marshal(map[string]any{
				"type":           "delete",
				"originDeviceId": c.deviceID,
				"payload":        json.RawMessage(in.Payload),
			})
			a.hub.broadcast(c.userID, c.deviceID, out)
			continue
		}
		if _, err := a.store.InsertMessage(c.userID, c.deviceID, in.Type, in.Payload); err != nil {
			log.Printf("insert message: %v", err)
			continue
		}
		msgs, err := a.store.MessagesSince(c.userID, 0)
		if err != nil || len(msgs) == 0 {
			continue
		}
		last := msgs[len(msgs)-1]
		out, _ := json.Marshal(map[string]any{
			"type":           last.Type,
			"originDeviceId": last.OriginDeviceID,
			"seq":            last.Seq,
			"ts":             last.Ts,
			"payload":        json.RawMessage(last.Payload),
		})
		a.hub.broadcast(c.userID, c.deviceID, out)
	}
}

func (c *conn) writeLoop() {
	ticker := time.NewTicker(wsPingPeriod)
	defer func() {
		ticker.Stop()
		c.ws.Close()
	}()
	for {
		select {
		case msg, ok := <-c.send:
			c.ws.SetWriteDeadline(time.Now().Add(wsWriteWait))
			if !ok {
				c.ws.WriteMessage(websocket.CloseMessage, []byte{})
				return
			}
			if err := c.ws.WriteMessage(websocket.TextMessage, msg); err != nil {
				return
			}
		case <-ticker.C:
			c.ws.SetWriteDeadline(time.Now().Add(wsWriteWait))
			if err := c.ws.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}
