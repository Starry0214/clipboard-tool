package main

import (
	"encoding/json"
	"flag"
	"log"
	"net/http"
)

type app struct {
	mux *http.ServeMux
}

func newApp() *app {
	a := &app{mux: http.NewServeMux()}
	a.mux.HandleFunc("GET /api/health", func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(map[string]bool{"ok": true})
	})
	return a
}

func main() {
	addr := flag.String("addr", "127.0.0.1:8082", "listen address")
	dbPath := flag.String("db", "sync.db", "sqlite database path")
	flag.Parse()
	a := newApp()
	log.Printf("sync server listening on %s (db=%s)", *addr, *dbPath)
	if err := http.ListenAndServe(*addr, a.mux); err != nil {
		log.Fatal(err)
	}
}
