# Google Cloud deployment

Marvin is deployed as two Cloud Run services:

- `marvin-site`: the authenticated web app, API, browser/device WebSockets, text agent and realtime voice agent. It deliberately runs as one warm instance until socket fan-out and distributed turn leases are implemented.
- `marvin-docs`: the public, cacheable build documentation. It can deploy and scale independently without interrupting conversations.

The app uses Cloud SQL for PostgreSQL and Secret Manager for the GitHub OAuth credentials, OpenAI API key, database URL and device-enrollment signing keys. The Cloud Run URL is the application origin and the GitHub OAuth callback is `<app origin>/auth/callback`. Configure the GitHub OAuth App with that exact callback before deploying.

First deploy the docs, then the app:

```sh
PROJECT_ID=your-project deploy/cloud/docs.sh
PROJECT_ID=your-project \
GITHUB_CLIENT_ID=... \
GITHUB_CLIENT_SECRET=... \
OPENAI_API_KEY=... \
deploy/cloud/app.sh
```

Later releases need only `PROJECT_ID`; existing managed secrets are reused. Supplying a credential again creates a new secret version. The app deployment is health-gated, so a failed new revision does not replace the serving revision.

Register each physical Pet's factory public key before it can be claimed. This intentionally keeps arbitrary or guessed device IDs out of the ownership flow. Run the existing `scripts/enrollment-admin.ts` command against the Cloud SQL database through an authenticated Cloud SQL connection; never copy the enrollment signing key or database password into the repository.
