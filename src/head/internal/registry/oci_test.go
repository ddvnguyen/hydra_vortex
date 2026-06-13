//go:build integration
// +build integration

package registry

import (
	"log/slog"
	"os"
	"testing"
)

func TestPullFromGHCR(t *testing.T) {
	if os.Getenv("OCI_TEST_PULL") == "" {
		t.Skip("set OCI_TEST_PULL=1 to run")
	}

	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	mgr := NewManager(logger, "/tmp/hydra-registry-cache")
	err := mgr.PullBinary(
		"ghcr.io/ddvnguyen/llama-server-sm60:69e9835ab",
		"/tmp/test-oci-llama-server",
		"",
		"llama-server",
	)
	if err != nil {
		t.Fatal(err)
	}

	info, err := os.Stat("/tmp/test-oci-llama-server")
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("pulled binary size: %d bytes (%d MB)", info.Size(), info.Size()/1024/1024)
}
