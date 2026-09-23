package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/coder/websocket"
)

const protocolVersion = 1

var (
	cliWorkspaceOnce sync.Once
	cliWorkspacePath string
	cliWorkspaceErr  error
)

type credentials struct {
	Server      string `json:"server"`
	ConnectorID string `json:"connectorId"`
	Credential  string `json:"credential"`
}

type message struct {
	V           int             `json:"v"`
	Type        string          `json:"type"`
	Token       string          `json:"token,omitempty"`
	Name        string          `json:"name,omitempty"`
	ConnectorID string          `json:"connectorId,omitempty"`
	Credential  string          `json:"credential,omitempty"`
	RequestID   string          `json:"requestId,omitempty"`
	Operation   string          `json:"operation,omitempty"`
	Body        json.RawMessage `json:"body,omitempty"`
	Deadline    int64           `json:"deadline,omitempty"`
}

type repository struct {
	ID           string   `json:"id"`
	Name         string   `json:"name"`
	Description  string   `json:"description"`
	Source       string   `json:"source"`
	Capabilities []string `json:"capabilities"`
	qualified    string
}

type repoPage struct {
	Items []struct {
		Repo   string `json:"repo"`
		Status string `json:"status"`
	} `json:"items"`
	NextPageToken string `json:"nextPageToken"`
}

type requestError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
	Status  int    `json:"status"`
}

func main() {
	server := flag.String("server", "", "Marvin connector WebSocket URL")
	pair := flag.String("pair", "", "one-time pairing code from Marvin")
	pairStdin := flag.Bool("pair-stdin", false, "read the one-time pairing code from standard input")
	name := flag.String("name", hostname(), "name shown in Marvin")
	entire := flag.String("entire", "entire", "Entire CLI path")
	contextName := flag.String("context", "", "optional Entire login context")
	credentialPath := flag.String("credentials", defaultCredentialPath(), "connector credential file")
	flag.Parse()
	if *pairStdin {
		if *pair != "" {
			fatal(errors.New("use only one pairing-code input method"))
		}
		raw, err := io.ReadAll(io.LimitReader(os.Stdin, 257))
		if err != nil || len(raw) > 256 {
			fatal(errors.New("could not read the pairing code"))
		}
		*pair = strings.TrimSpace(string(raw))
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	stored, _ := loadCredentials(*credentialPath)
	if *server == "" && stored != nil {
		*server = stored.Server
	}
	if err := validateServer(*server); err != nil {
		fatal(err)
	}
	if _, err := exec.LookPath(*entire); err != nil {
		fatal(fmt.Errorf("Entire CLI not found: %w", err))
	}

	for ctx.Err() == nil {
		if err := run(ctx, *server, *pair, *name, *entire, *contextName, *credentialPath, stored); err != nil && ctx.Err() == nil {
			fmt.Fprintln(os.Stderr, "Connector offline:", err)
			time.Sleep(2 * time.Second)
			stored, _ = loadCredentials(*credentialPath)
			if stored != nil && stored.Server == *server {
				*pair = ""
			}
			continue
		}
		return
	}
}

func run(ctx context.Context, server, pair, name, entire, contextName, credentialPath string, stored *credentials) error {
	c, _, err := websocket.Dial(ctx, server, &websocket.DialOptions{HTTPHeader: http.Header{"User-Agent": []string{"marvin-entire-connector/1"}}})
	if err != nil {
		return err
	}
	defer c.Close(websocket.StatusNormalClosure, "Connector stopped")
	c.SetReadLimit(1 << 20)

	if pair != "" {
		err = writeJSON(ctx, c, message{V: protocolVersion, Type: "pair", Token: pair, Name: name})
	} else if stored != nil && stored.Server == server {
		err = writeJSON(ctx, c, message{V: protocolVersion, Type: "hello", ConnectorID: stored.ConnectorID, Credential: stored.Credential})
	} else {
		return errors.New("no saved connector credential; create a pairing code in Marvin and pass --pair")
	}
	if err != nil {
		return err
	}

	for {
		_, raw, err := c.Read(ctx)
		if err != nil {
			return err
		}
		var msg message
		if err := json.Unmarshal(raw, &msg); err != nil || msg.V != protocolVersion {
			return errors.New("Marvin sent an unsupported connector message")
		}
		switch msg.Type {
		case "paired":
			stored = &credentials{Server: server, ConnectorID: msg.ConnectorID, Credential: msg.Credential}
			if err := saveCredentials(credentialPath, stored); err != nil {
				return fmt.Errorf("save connector credential: %w", err)
			}
			fmt.Println("Paired with Marvin. The connector can now reconnect without the one-time code.")
		case "ready":
			fmt.Println("Marvin Entire Connector is online.")
		case "request":
			handleRequest(ctx, c, msg, entire, contextName)
		case "cancel":
			// Command deadlines are independently bounded. Cancellation is best-effort
			// in this first protocol version and a late result is ignored server-side.
		default:
			return fmt.Errorf("unknown message type %q", msg.Type)
		}
	}
}

func handleRequest(parent context.Context, c *websocket.Conn, msg message, entire, contextName string) {
	deadline := time.UnixMilli(msg.Deadline)
	if deadline.Before(time.Now()) || deadline.After(time.Now().Add(65*time.Second)) {
		deadline = time.Now().Add(60 * time.Second)
	}
	ctx, cancel := context.WithDeadline(parent, deadline)
	defer cancel()
	value, reqErr := execute(ctx, msg.Operation, msg.Body, entire, contextName)
	if reqErr != nil {
		fmt.Fprintln(os.Stderr, "Entire request failed:", reqErr.Message)
	}
	response := map[string]any{"v": protocolVersion, "type": "result", "requestId": msg.RequestID, "ok": reqErr == nil}
	if reqErr == nil {
		response["value"] = value
	} else {
		response["error"] = reqErr
	}
	writeCtx, writeCancel := context.WithTimeout(parent, 5*time.Second)
	defer writeCancel()
	_ = writeJSON(writeCtx, c, response)
}

func execute(ctx context.Context, operation string, body json.RawMessage, entire, contextName string) (any, *requestError) {
	switch operation {
	case "configured", "connect":
		repos, _, err := listRepositories(ctx, entire, contextName, "")
		return repos, classify(err)
	case "discover":
		var input struct {
			Cursor string `json:"cursor"`
		}
		if err := json.Unmarshal(body, &input); err != nil {
			return nil, badRequest("Invalid repository cursor.")
		}
		repos, cursor, err := listRepositories(ctx, entire, contextName, input.Cursor)
		return map[string]any{"items": repos, "nextCursor": nullable(cursor)}, classify(err)
	case "check":
		var input struct {
			RepositoryID string `json:"repositoryId"`
		}
		if json.Unmarshal(body, &input) != nil {
			return nil, badRequest("Invalid repository selection.")
		}
		repo, err := findRepository(ctx, entire, contextName, input.RepositoryID)
		return map[string]any{"id": repo.ID}, classify(err)
	case "read":
		var input struct {
			RepositoryID string         `json:"repositoryId"`
			Tool         string         `json:"tool"`
			Args         map[string]any `json:"args"`
		}
		if json.Unmarshal(body, &input) != nil {
			return nil, badRequest("Invalid repository read.")
		}
		repo, err := findRepository(ctx, entire, contextName, input.RepositoryID)
		if err != nil {
			return nil, classify(err)
		}
		return readRepository(ctx, entire, contextName, repo, input.Tool, input.Args)
	default:
		return nil, &requestError{Code: "TOOL_FORBIDDEN", Message: "Unknown connector operation.", Status: 403}
	}
}

func listRepositories(ctx context.Context, entire, contextName, cursor string) ([]repository, string, error) {
	args := []string{"repo", "mirror", "list", "--mirrored", "--json", "--page-size", "100"}
	if cursor != "" {
		args = append(args, "--page-token", cursor)
	}
	raw, err := runEntire(ctx, entire, contextName, args...)
	if err != nil {
		return nil, "", err
	}
	var page repoPage
	if err := json.Unmarshal(raw, &page); err != nil {
		return nil, "", errors.New("Entire returned unsupported repository data")
	}
	repos := make([]repository, 0, len(page.Items))
	for _, row := range page.Items {
		q := strings.TrimPrefix(row.Repo, "/")
		name := strings.TrimPrefix(strings.TrimPrefix(q, "gh/"), "et/")
		repos = append(repos, repository{ID: repositoryID(q), Name: name, Description: "Entire connector repository · " + row.Status, Source: "entire", Capabilities: capabilities(q), qualified: q})
	}
	return repos, page.NextPageToken, nil
}

func findRepository(ctx context.Context, entire, contextName, id string) (repository, error) {
	repos, cursor, err := listRepositories(ctx, entire, contextName, "")
	for pages := 0; err == nil && pages < 9; pages++ {
		for _, repo := range repos {
			if repo.ID == id {
				return repo, nil
			}
		}
		if cursor == "" {
			break
		}
		repos, cursor, err = listRepositories(ctx, entire, contextName, cursor)
	}
	if err != nil {
		return repository{}, err
	}
	return repository{}, errors.New("repository unavailable")
}

func readRepository(ctx context.Context, entire, contextName string, repo repository, tool string, args map[string]any) (any, *requestError) {
	base := map[string]any{"kind": "repository", "repositoryId": repo.ID, "source": "entire", "revision": "Entire cloud index; commit revision unavailable", "files": []any{}, "fetchedAt": time.Now().UnixMilli()}
	switch tool {
	case "repository_summary":
		base["title"], base["summary"], base["capabilities"], base["evidence"] = repo.Name, "Entire access is verified through this computer. Search the index for code and development history.", repo.Capabilities, []any{}
		return base, nil
	case "repository_search":
		query, qOK := args["query"].(string)
		mode, mOK := args["mode"].(string)
		page, pOK := number(args["page"])
		if !qOK || !mOK || !pOK || len(query) == 0 || len(query) > 300 || page < 1 || page > 20 || strings.HasPrefix(query, "-") {
			return nil, badRequest("Invalid search arguments.")
		}
		command := []string{"search", query, "--repo", repo.qualified, "--limit", "10"}
		if mode == "code" {
			command = append(command, "--code", "--json")
		} else if mode == "history" {
			command = append(command, "--page", fmt.Sprint(page), "--compact")
		} else {
			return nil, badRequest("Invalid search mode.")
		}
		raw, err := runEntire(ctx, entire, contextName, command...)
		if err != nil {
			return nil, classify(err)
		}
		var result map[string]any
		if json.Unmarshal(raw, &result) != nil {
			return nil, &requestError{Code: "ENTIRE_BAD_RESPONSE", Message: "Entire returned unreadable search data.", Status: 502}
		}
		evidence := []map[string]any{}
		rows, _ := result["results"].([]any)
		for i, item := range rows {
			if i >= 10 {
				break
			}
			row, _ := item.(map[string]any)
			if mode == "history" && !withinRequestedWindow(query, row["date"], time.Now()) {
				continue
			}
			returnedRepo, _ := row["repo"].(string)
			if returnedRepo == "" || displayRepository(returnedRepo) != repo.Name {
				return nil, &requestError{Code: "ENTIRE_SCOPE_MISMATCH", Message: "Entire returned results from another repository.", Status: 502}
			}
			if mode == "code" {
				path, _ := row["path"].(string)
				if !safeSourcePath(path) {
					continue
				}
				line, _ := number(row["line"])
				excerpt, _ := row["context_line"].(string)
				evidence = append(evidence, map[string]any{"id": fmt.Sprintf("code-%d", i+1), "label": path, "path": path, "lineStart": line, "lineEnd": line, "excerpt": bounded(excerpt, 1600), "origin": "entire"})
			} else {
				id, _ := row["id"].(string)
				title, _ := row["title"].(string)
				snippet, _ := row["snippet"].(string)
				kind, _ := row["type"].(string)
				date, _ := row["date"].(string)
				evidence = append(evidence, map[string]any{"id": bounded(id, 128), "label": bounded(title, 200), "excerpt": bounded(first(snippet, title), 1600), "origin": "entire", "kind": bounded(kind, 40), "date": bounded(date, 80)})
			}
		}
		base["title"], base["summary"], base["evidence"] = map[bool]string{true: "Code search", false: "Repository history search"}[mode == "code"], "Indexed Entire results. Verify current source before relying on them.", evidence
		if total, ok := number(result["total_pages"]); ok && page < total {
			base["nextPage"] = page + 1
		} else {
			base["nextPage"] = nil
		}
		return base, nil
	case "repository_checkpoint":
		id, ok := args["id"].(string)
		if !ok || len(id) == 0 || len(id) > 128 || strings.HasPrefix(repo.qualified, "et/") {
			return nil, badRequest("Invalid checkpoint identifier.")
		}
		raw, err := runEntire(ctx, entire, contextName, "checkpoint", "explain", id, "--repo", repo.Name, "--json", "--no-pager")
		if err != nil {
			return nil, classify(err)
		}
		var value map[string]any
		if json.Unmarshal(raw, &value) != nil || value["checkpoint_id"] != id {
			return nil, &requestError{Code: "ENTIRE_SCOPE_MISMATCH", Message: "Entire returned unsupported checkpoint data.", Status: 502}
		}
		base["title"], base["summary"], base["evidence"] = "Checkpoint "+id, "Stored checkpoint metadata. Full transcripts are not retrieved.", []any{}
		return base, nil
	default:
		return nil, &requestError{Code: "ENTIRE_CAPABILITY_UNAVAILABLE", Message: "This operation needs a separately authorized source checkout.", Status: 409}
	}
}

func runEntire(ctx context.Context, binary, contextName string, args ...string) ([]byte, error) {
	workspace, err := entireCLIWorkspace()
	if err != nil {
		return nil, err
	}
	if repo := repositoryArgument(args); repo != "" {
		if err = configureWorkspaceOrigin(workspace, repo); err != nil {
			return nil, err
		}
	}
	if contextName != "" {
		args = append([]string{"--context", contextName}, args...)
	}
	for attempt := 0; attempt < 2; attempt++ {
		cmd := exec.CommandContext(ctx, binary, args...)
		cmd.Dir = workspace
		cmd.Env = append(os.Environ(), "ENTIRE_TELEMETRY_OPTOUT=1", "GIT_TERMINAL_PROMPT=0", "GIT_PAGER=cat", "PAGER=cat", "NO_COLOR=1")
		output, err := cmd.Output()
		if err == nil {
			if len(output) > 512*1024 {
				return nil, errors.New("Entire response exceeded the connector limit")
			}
			return output, nil
		}
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		diagnostic := err.Error()
		if exit, ok := err.(*exec.ExitError); ok {
			diagnostic = bounded(string(exit.Stderr), 300)
		}
		if attempt == 0 && transientEntireError(diagnostic) {
			select {
			case <-time.After(250 * time.Millisecond):
				continue
			case <-ctx.Done():
				return nil, ctx.Err()
			}
		}
		return nil, errors.New(diagnostic)
	}
	return nil, errors.New("Entire request failed")
}

func withinRequestedWindow(query string, rawDate any, now time.Time) bool {
	lower := strings.ToLower(query)
	window := time.Duration(0)
	if strings.Contains(lower, "date:week") {
		window = 7 * 24 * time.Hour
	} else if strings.Contains(lower, "date:month") {
		window = 31 * 24 * time.Hour
	} else {
		return true
	}
	date, ok := rawDate.(string)
	if !ok {
		return false
	}
	parsed, err := time.Parse(time.RFC3339Nano, date)
	if err != nil {
		return false
	}
	return !parsed.Before(now.Add(-window)) && !parsed.After(now.Add(5*time.Minute))
}

func repositoryArgument(args []string) string {
	for index, arg := range args {
		if arg == "--repo" && index+1 < len(args) {
			return args[index+1]
		}
	}
	return ""
}

func configureWorkspaceOrigin(workspace, qualified string) error {
	repo := displayRepository(qualified)
	parts := strings.Split(repo, "/")
	if len(parts) != 2 || parts[0] == "" || parts[1] == "" || strings.ContainsAny(repo, "\x00\r\n") {
		return errors.New("Entire returned an invalid repository name")
	}
	remoteURL := "https://github.com/" + repo + ".git"
	current := exec.Command("git", "-C", workspace, "remote", "get-url", "origin")
	current.Env = append(os.Environ(), "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null")
	output, err := current.Output()
	if err == nil && strings.TrimSpace(string(output)) == remoteURL {
		return nil
	}
	action := "add"
	if err == nil {
		action = "set-url"
	}
	update := exec.Command("git", "-C", workspace, "remote", action, "origin", remoteURL)
	update.Env = append(os.Environ(), "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null")
	if output, err = update.CombinedOutput(); err != nil {
		return fmt.Errorf("configure connector workspace: %s", bounded(string(output), 200))
	}
	return nil
}

func entireCLIWorkspace() (string, error) {
	cliWorkspaceOnce.Do(func() {
		cache, err := os.UserCacheDir()
		if err != nil {
			cliWorkspaceErr = fmt.Errorf("locate connector cache: %w", err)
			return
		}
		cliWorkspacePath = filepath.Join(cache, "Marvin Entire Connector", "cli-workspace")
		if err = os.MkdirAll(cliWorkspacePath, 0700); err != nil {
			cliWorkspaceErr = fmt.Errorf("create connector workspace: %w", err)
			return
		}
		if _, err = os.Stat(filepath.Join(cliWorkspacePath, ".git")); err == nil {
			return
		} else if !errors.Is(err, os.ErrNotExist) {
			cliWorkspaceErr = fmt.Errorf("inspect connector workspace: %w", err)
			return
		}
		init := exec.Command("git", "init", "--quiet", cliWorkspacePath)
		init.Env = append(os.Environ(), "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null")
		if output, err := init.CombinedOutput(); err != nil {
			cliWorkspaceErr = fmt.Errorf("initialize connector workspace: %s", bounded(string(output), 200))
		}
	})
	return cliWorkspacePath, cliWorkspaceErr
}

func transientEntireError(value string) bool {
	text := strings.ToLower(value)
	return strings.Contains(text, "i/o timeout") || strings.Contains(text, "temporary failure") || strings.Contains(text, "connection reset") || strings.Contains(text, "connection refused") || strings.Contains(text, "unexpected eof")
}

func capabilities(q string) []string {
	result := []string{"summary", "search", "code_search"}
	if !strings.HasPrefix(q, "et/") {
		result = append(result, "checkpoint")
	}
	return result
}
func displayRepository(value string) string {
	value = strings.TrimPrefix(value, "/")
	value = strings.TrimPrefix(value, "gh/")
	return strings.TrimPrefix(value, "et/")
}
func safeSourcePath(value string) bool {
	if value == "" || len(value) > 300 || strings.HasPrefix(value, "/") || strings.Contains(value, "\\") || strings.ContainsAny(value, "\x00\r\n") {
		return false
	}
	for _, part := range strings.Split(value, "/") {
		lower := strings.ToLower(part)
		if part == "." || part == ".." || lower == ".git" || strings.HasPrefix(lower, ".env") || lower == ".ssh" || lower == ".npmrc" || lower == "credentials" || strings.HasSuffix(lower, ".pem") || strings.HasSuffix(lower, ".key") || strings.HasSuffix(lower, ".p12") || lower == "id_rsa" || lower == "id_ed25519" {
			return false
		}
	}
	return true
}
func repositoryID(q string) string {
	sum := sha256.Sum256([]byte(q))
	return "entire_" + hex.EncodeToString(sum[:])[:24]
}
func nullable(value string) any {
	if value == "" {
		return nil
	}
	return value
}
func number(value any) (int, bool) {
	switch n := value.(type) {
	case float64:
		return int(n), n == float64(int(n))
	case int:
		return n, true
	default:
		return 0, false
	}
}
func first(a, b string) string {
	if a != "" {
		return a
	}
	return b
}
func bounded(value string, max int) string {
	value = strings.ReplaceAll(value, "\x00", "")
	if len(value) > max {
		return value[:max]
	}
	return value
}
func badRequest(message string) *requestError {
	return &requestError{Code: "TOOL_ARGUMENTS", Message: message, Status: 400}
}
func classify(err error) *requestError {
	if err == nil {
		return nil
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return &requestError{Code: "ENTIRE_CONNECTOR_TIMEOUT", Message: "Entire took too long to answer. Try the repository question again.", Status: 504}
	}
	text := strings.ToLower(err.Error())
	if strings.Contains(text, "not logged in") || strings.Contains(text, "401") || strings.Contains(text, "login required") {
		return &requestError{Code: "ENTIRE_REAUTH_REQUIRED", Message: "Sign in to Entire on this computer.", Status: 409}
	}
	if strings.Contains(text, "403") || strings.Contains(text, "forbidden") || strings.Contains(text, "not found") || strings.Contains(text, "unavailable") {
		return &requestError{Code: "REPOSITORY_FORBIDDEN", Message: "This repository is unavailable.", Status: 403}
	}
	if strings.Contains(text, "429") || strings.Contains(text, "rate limit") {
		return &requestError{Code: "ENTIRE_RATE_LIMITED", Message: "Entire is limiting requests.", Status: 429}
	}
	return &requestError{Code: "ENTIRE_CONNECTOR_FAILED", Message: "The local Entire CLI could not complete this read.", Status: 502}
}
func writeJSON(ctx context.Context, c *websocket.Conn, value any) error {
	raw, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return c.Write(ctx, websocket.MessageText, raw)
}
func defaultCredentialPath() string {
	if runtime.GOOS == "darwin" {
		home, _ := os.UserHomeDir()
		return filepath.Join(home, "Library", "Application Support", "Marvin Entire Connector", "credential.json")
	}
	dir, err := os.UserConfigDir()
	if err != nil {
		return "marvin-entire-connector.json"
	}
	return filepath.Join(dir, "marvin-entire-connector", "credential.json")
}
func loadCredentials(path string) (*credentials, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var value credentials
	return &value, json.Unmarshal(raw, &value)
}
func saveCredentials(path string, value *credentials) error {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	raw, err := json.Marshal(value)
	if err != nil {
		return err
	}
	temp := path + ".tmp"
	if err = os.WriteFile(temp, raw, 0600); err != nil {
		return err
	}
	return os.Rename(temp, path)
}
func validateServer(raw string) error {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || u.Path != "/api/entire/connector/socket" {
		return errors.New("--server must be the Marvin connector WebSocket URL")
	}
	if u.Scheme != "wss" && !((u.Scheme == "ws") && (u.Hostname() == "127.0.0.1" || u.Hostname() == "localhost")) {
		return errors.New("connector URL must use wss:// (ws:// is allowed only for localhost)")
	}
	return nil
}
func hostname() string {
	name, err := os.Hostname()
	if err != nil || name == "" {
		return "My computer"
	}
	return name
}
func fatal(err error) { fmt.Fprintln(os.Stderr, "Error:", err); os.Exit(1) }
