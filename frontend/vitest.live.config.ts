import { defineConfig } from 'vitest/config';

// Separate config for the *.live.test.ts acceptance scenario (frontend spec §16: "do not use
// mocked backend responses for the main acceptance scenario if the environment allows a live
// backend"). Needs the real API reachable at VITE_API_BASE_URL (default http://localhost:5288,
// or http://localhost:5080 against the docker-compose bpm-api) with an admin account seeded —
// run `docker compose up -d postgres redis minio bpm-api` first. Node environment, not jsdom —
// this test talks to the API directly over HTTP, no React rendering involved.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['**/*.live.test.ts'],
    // Phase 6.2: notification.delivery.live.test.ts polls real background-worker delivery state
    // through several retry/backoff cycles (EmailDeliveryWorker's own poll interval plus this
    // repo's docker-compose dev backoff settings) — 20s was comfortably enough for every
    // pre-6.2 scenario but not for a bounded multi-attempt retry wait; raised for all live tests
    // rather than per-file so the margin is consistent.
    testTimeout: 40000,
  },
});
