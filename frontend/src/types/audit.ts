// Mirrors BPM.Application.Audit.AuditLogDto/AuditLogQuery. Read-only — there is no create/update/
// delete DTO because the Audit Logs workspace is strictly read-only (Phase 5.5.2 §16-17).
export interface AuditLogEntry {
  id: string;
  userId: string | null;
  action: string;
  entityType: string;
  entityId: string | null;
  oldValue: string | null;
  newValue: string | null;
  timestamp: string;
}

export interface AuditLogQuery {
  userId?: string;
  entityType?: string;
  entityId?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}
