package health

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"sync"
	"time"
)

type Mode string

const (
	ModeIdle Mode = "idle"
	ModeBusy Mode = "busy"
)

type SlotStatus struct {
	ID           int  `json:"id"`
	IsProcessing bool `json:"is_processing"`
	NPast        int  `json:"n_past"`
	NRemain      int  `json:"n_remain"`
}

type Checker struct {
	baseURL       string
	logger        *slog.Logger
	client        *http.Client
	mode          Mode
	consecutiveFails int
	maxFails      int
	idleInterval  time.Duration
	busyInterval  time.Duration
	ctx           context.Context
	cancel        context.CancelFunc
	mu            sync.RWMutex
	onUnhealthy   func()
}

func NewChecker(baseURL string, logger *slog.Logger, idleInterval, busyInterval time.Duration, maxFails int) *Checker {
	ctx, cancel := context.WithCancel(context.Background())
	return &Checker{
		baseURL:      baseURL,
		logger:       logger,
		client:       &http.Client{Timeout: 5 * time.Second},
		mode:         ModeIdle,
		maxFails:     maxFails,
		idleInterval: idleInterval,
		busyInterval: busyInterval,
		ctx:          ctx,
		cancel:       cancel,
	}
}

func (c *Checker) SetOnUnhealthy(fn func()) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.onUnhealthy = fn
}

func (c *Checker) Start() {
	go c.run()
}

func (c *Checker) Stop() {
	c.cancel()
}

func (c *Checker) run() {
	c.logger.Info("health checker started", "mode", c.mode)

	for {
		select {
		case <-c.ctx.Done():
			c.logger.Info("health checker stopped")
			return
		case <-time.After(c.getInterval()):
			c.check()
		}
	}
}

func (c *Checker) getInterval() time.Duration {
	c.mu.RLock()
	defer c.mu.RUnlock()
	if c.mode == ModeIdle {
		return c.idleInterval
	}
	return c.busyInterval
}

func (c *Checker) check() {
	slots, err := c.getSlots()
	if err != nil {
		c.mu.Lock()
		c.consecutiveFails++
		fails := c.consecutiveFails
		c.mu.Unlock()

		c.logger.Warn("health check failed",
			"fails", fails,
			"max", c.maxFails,
			"error", err)

		if fails >= c.maxFails {
			c.logger.Error("max failures reached, triggering restart")
			c.mu.RLock()
			if c.onUnhealthy != nil {
				c.onUnhealthy()
			}
			c.mu.RUnlock()
			c.mu.Lock()
			c.consecutiveFails = 0
			c.mu.Unlock()
		}
		return
	}

	c.mu.Lock()
	c.consecutiveFails = 0
	c.mu.Unlock()

	isBusy := false
	for _, slot := range slots {
		if slot.IsProcessing && slot.NRemain > 0 {
			isBusy = true
			break
		}
	}

	c.mu.Lock()
	oldMode := c.mode
	if isBusy {
		c.mode = ModeBusy
	} else {
		c.mode = ModeIdle
	}
	newMode := c.mode
	c.mu.Unlock()

	if oldMode != newMode {
		c.logger.Info("health mode changed", "from", oldMode, "to", newMode)
	}
}

func (c *Checker) getSlots() ([]SlotStatus, error) {
	resp, err := c.client.Get(c.baseURL + "/slots")
	if err != nil {
		return nil, fmt.Errorf("GET /slots: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("GET /slots returned %d", resp.StatusCode)
	}

	var slots []SlotStatus
	if err := json.NewDecoder(resp.Body).Decode(&slots); err != nil {
		return nil, fmt.Errorf("decode slots: %w", err)
	}

	return slots, nil
}

func (c *Checker) GetMode() Mode {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.mode
}

func (c *Checker) IsHealthy() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.consecutiveFails < c.maxFails
}
