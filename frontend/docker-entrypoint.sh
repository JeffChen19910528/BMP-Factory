#!/bin/sh
# Runs automatically via nginx's own /docker-entrypoint.d/ mechanism before nginx starts.
# Vite bakes VITE_* vars into the JS bundle at build time, but this image is built once and
# reused across environments, so the actual API URL must come from the container's runtime env
# instead — see src/lib/runtimeEnv.ts for how the frontend reads window.__ENV__.
set -eu

cat > /usr/share/nginx/html/env-config.js <<EOF
window.__ENV__ = {
  VITE_API_BASE_URL: "${VITE_API_BASE_URL:-}"
};
EOF
