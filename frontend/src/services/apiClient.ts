import axios from 'axios';
import { getApiBaseUrl } from '../lib/runtimeEnv';
import { useAuthStore } from '../stores/authStore';
import type { ApiErrorBody } from '../types/api';

export const apiClient = axios.create({
  baseURL: getApiBaseUrl(),
});

apiClient.interceptors.request.use((config) => {
  const token = useAuthStore.getState().accessToken;
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      useAuthStore.getState().clearSession();
    }
    return Promise.reject(error);
  },
);

// Normalizes axios' error shape into the backend's {code, message, traceId} body so callers
// don't need to know about AxiosError internals — see ErrorHandlingMiddleware for the source
// shape. Not every error actually has that shape: ASP.NET Core's built-in auth pipeline rejects
// missing/invalid tokens (401) and role mismatches (403) *before* any controller or
// ErrorHandlingMiddleware code runs, so those come back with an empty body — only a business-
// logic ForbiddenAppException (e.g. an IDOR check inside a service) gets the real {code,message}
// shape on a 403. Fall back to a sensible message per status code when there's no body to read.
export function toApiError(error: unknown): ApiErrorBody {
  if (axios.isAxiosError(error)) {
    if (error.response?.data && typeof error.response.data === 'object' && 'code' in error.response.data) {
      return error.response.data as ApiErrorBody;
    }

    switch (error.response?.status) {
      case 401:
        return { code: 'UNAUTHORIZED', message: 'Your session has expired. Please log in again.', traceId: '' };
      case 403:
        return { code: 'FORBIDDEN', message: "You don't have permission to perform this action.", traceId: '' };
      case 404:
        return { code: 'NOT_FOUND', message: 'The requested resource was not found.', traceId: '' };
      case 409:
        return { code: 'CONFLICT', message: 'This action conflicts with the current state of the resource.', traceId: '' };
    }

    if (!error.response) {
      return { code: 'NETWORK_ERROR', message: 'Could not reach the server. Check your connection and try again.', traceId: '' };
    }
  }

  return { code: 'UNKNOWN_ERROR', message: 'An unexpected error occurred.', traceId: '' };
}
