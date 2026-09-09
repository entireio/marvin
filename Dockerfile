# syntax=docker/dockerfile:1
#
# The Marvin website, wrapped in the GitHub sign-in gate from server/.
# Built by `deploy_google_cloud.sh`; see server/README.md for what it runs.

# --- build -----------------------------------------------------------------
FROM golang:1.24-alpine AS build
WORKDIR /src

# The server has no third-party dependencies, so go.mod is the entire
# dependency story and there is nothing to download.
COPY server/ ./
# Static binary: no cgo means no libc, which is what lets the final stage be
# distroless. -trimpath keeps build paths out of the binary.
ENV CGO_ENABLED=0
RUN go build -trimpath -ldflags="-s -w" -o /out/marvin-site .

# --- run -------------------------------------------------------------------
# distroless/static carries a CA bundle (needed to reach github.com) and a
# nonroot user, and nothing else — no shell, no package manager.
FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/marvin-site /usr/local/bin/marvin-site
# The site is copied verbatim, so what you review here is exactly what GitHub
# Pages would publish later — including docs/README.md and the design brief.
COPY docs/ /srv/docs/

ENV DOCS_DIR=/srv/docs \
    PORT=8080
EXPOSE 8080
USER nonroot:nonroot
ENTRYPOINT ["/usr/local/bin/marvin-site"]
