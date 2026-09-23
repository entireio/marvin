package main

import (
	"os/exec"
	"strings"
	"testing"
	"time"
)

func TestRepositoryArgument(t *testing.T) {
	if got := repositoryArgument([]string{"search", "changes", "--repo", "spedemon/marvin", "--compact"}); got != "spedemon/marvin" {
		t.Fatalf("repositoryArgument() = %q", got)
	}
	if got := repositoryArgument([]string{"repo", "mirror", "list"}); got != "" {
		t.Fatalf("repositoryArgument() without --repo = %q", got)
	}
}

func TestWithinRequestedWindow(t *testing.T) {
	now := time.Date(2026, 9, 23, 12, 0, 0, 0, time.UTC)
	if !withinRequestedWindow("changes date:week", "2026-09-18T12:00:00Z", now) {
		t.Fatal("recent result was excluded")
	}
	if withinRequestedWindow("changes date:week", "2026-09-09T12:00:00Z", now) {
		t.Fatal("older result was included")
	}
	if !withinRequestedWindow("changes", "2026-09-09T12:00:00Z", now) {
		t.Fatal("unbounded query was filtered")
	}
}

func TestConfigureWorkspaceOrigin(t *testing.T) {
	workspace := t.TempDir()
	if output, err := exec.Command("git", "init", "--quiet", workspace).CombinedOutput(); err != nil {
		t.Fatalf("git init: %v: %s", err, output)
	}
	if err := configureWorkspaceOrigin(workspace, "gh/spedemon/marvin"); err != nil {
		t.Fatal(err)
	}
	output, err := exec.Command("git", "-C", workspace, "remote", "get-url", "origin").Output()
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.TrimSpace(string(output)); got != "https://github.com/spedemon/marvin.git" {
		t.Fatalf("origin = %q", got)
	}
	if err = configureWorkspaceOrigin(workspace, "other/widget"); err != nil {
		t.Fatal(err)
	}
	output, err = exec.Command("git", "-C", workspace, "remote", "get-url", "origin").Output()
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.TrimSpace(string(output)); got != "https://github.com/other/widget.git" {
		t.Fatalf("updated origin = %q", got)
	}
}
