package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
)

const maxMediaBytes = 50 * 1024 * 1024

func (a *app) handleMediaUpload(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	if r.ContentLength > maxMediaBytes {
		writeError(w, http.StatusRequestEntityTooLarge, "media too large")
		return
	}
	data, err := io.ReadAll(io.LimitReader(r.Body, maxMediaBytes+1))
	if err != nil || len(data) > maxMediaBytes {
		writeError(w, http.StatusRequestEntityTooLarge, "media too large")
		return
	}
	id, err := a.store.InsertMedia(dev.UserID, data)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	w.WriteHeader(http.StatusCreated)
	json.NewEncoder(w).Encode(map[string]int64{"mediaId": id})
}

func (a *app) handleMediaDownload(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeError(w, http.StatusBadRequest, "bad id")
		return
	}
	data, err := a.store.GetMedia(dev.UserID, id)
	if err != nil {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	fmt.Fprint(w, string(data))
}
