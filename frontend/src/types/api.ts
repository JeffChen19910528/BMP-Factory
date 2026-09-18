// Mirrors the uniform error shape from ErrorHandlingMiddleware (src/BPM.Api/ErrorHandlingMiddleware.cs).
export interface ApiFieldError {
  code: string;
  message: string;
}

export interface ApiErrorBody {
  code: string;
  message: string;
  errors?: ApiFieldError[];
  traceId: string;
}
