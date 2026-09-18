// Vite inlines import.meta.env.* at build time, but the docker-compose bpm-web container needs
// its API base URL set at *container start* (the same built image is meant to be reusable across
// environments). docker-entrypoint.sh writes window.__ENV__ into env-config.js from the
// container's actual environment before nginx serves the app; that takes priority when present.
declare global {
  interface Window {
    __ENV__?: Record<string, string>;
  }
}

export function getApiBaseUrl(): string {
  return (
    window.__ENV__?.VITE_API_BASE_URL ||
    import.meta.env.VITE_API_BASE_URL ||
    'http://localhost:5288'
  );
}
