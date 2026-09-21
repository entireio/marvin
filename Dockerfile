# syntax=docker/dockerfile:1
#
# Marvin's backend: the project website behind a GitHub sign-in, the web
# controller, and the WebSocket endpoint the robot itself connects to — one
# binary, one image, one deployment. See server/README.md.

# --- build -----------------------------------------------------------------
FROM golang:1.24-alpine AS build
WORKDIR /src

# Dependencies first, so that editing the server does not re-download them.
# There is exactly one: a WebSocket library. Everything else is the standard
# library.
COPY server/go.mod server/go.sum ./
RUN go mod download

COPY server/ ./
# Static binary: no cgo means no libc, which is what lets the final stage be
# distroless. -trimpath keeps build paths out of the binary.
ENV CGO_ENABLED=0
RUN go build -trimpath -ldflags="-s -w" -o /out/marvin-site .

# --- run -------------------------------------------------------------------
# distroless/static carries a CA bundle (needed to reach github.com and the AI
# providers) and a nonroot user, and nothing else — no shell, no package
# manager.
FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/marvin-site /usr/local/bin/marvin-site
# The site is copied verbatim, so what you review here is exactly what GitHub
# Pages would publish later — including docs/README.md and the design brief.
COPY docs/ /srv/docs/
# The controller is served from disk rather than embedded, for the same reason
# the site is: one pattern, and both can be swapped without a rebuild.
COPY controller/ /srv/controller/

ENV DOCS_DIR=/srv/docs \
    CONTROLLER_DIR=/srv/controller \
    PORT=8080
EXPOSE 8080
USER nonroot:nonroot
ENTRYPOINT ["/usr/local/bin/marvin-site"]
