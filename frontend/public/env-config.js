// Placeholder for local dev / `npm run build` without Docker — the real values come from
// import.meta.env (see .env.example). docker-entrypoint.sh overwrites this file at container
// start with the actual runtime environment (see src/lib/runtimeEnv.ts for why).
window.__ENV__ = {};
