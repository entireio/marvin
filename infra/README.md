# Deployment foundations

SQLite is the local default; DATABASE_URL selects PostgreSQL. CI runs both persistence contracts and browser/firmware builds. Production requires TLS with WebSocket forwarding, approved OIDC or local passphrase auth, real provider configuration and protected persistent storage. No cloud infrastructure has been deployed. Backup/restore, distributed event delivery, metrics and release packaging remain later milestones.
