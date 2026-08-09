package main

import (
	"time"
)

const retentionMs = 7 * 24 * 60 * 60 * 1000

func startCleanup(s *Store, stop <-chan struct{}) {
	go func() {
		s.Cleanup(time.Now().UnixMilli() - retentionMs)
		ticker := time.NewTicker(24 * time.Hour)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				s.Cleanup(time.Now().UnixMilli() - retentionMs)
			case <-stop:
				return
			}
		}
	}()
}
