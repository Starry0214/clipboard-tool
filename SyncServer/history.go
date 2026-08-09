package main

import (
	"encoding/json"
	"net/http"
	"strconv"
)

func (a *app) handleHistory(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	since, _ := strconv.ParseInt(r.URL.Query().Get("since"), 10, 64)
	msgs, err := a.store.MessagesSince(dev.UserID, since)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	json.NewEncoder(w).Encode(map[string][]Message{"messages": msgs})
}
