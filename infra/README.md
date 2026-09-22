# Deployment foundations

SQLite is the local default; `DATABASE_URL` selects PostgreSQL. The Google Cloud deployment uses Cloud Run, Cloud SQL and Secret Manager, with separate application and documentation releases under [`deploy/cloud`](../deploy/cloud/README.md). The realtime service remains limited to one instance until distributed socket fan-out is implemented. Backup recovery exercises, monitoring alerts and multi-instance event delivery remain follow-up operational work.
