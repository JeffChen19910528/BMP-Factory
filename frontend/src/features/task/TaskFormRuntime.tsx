import { useEffect, useState } from 'react';
import { Alert, Button, message } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import { useTranslation } from '../../i18n/LanguageContext';
import { toApiError } from '../../services/apiClient';
import type { WorkflowValidationResult } from '../../types/form';
import { FormRuntime } from '../form/runtime/FormRuntime';
import { useFormInstance, useFormInstanceData, useFormInstanceSchema, useSaveFormInstanceData, useSubmitFormInstance } from '../form/runtime/hooks';

const FORM_CONCURRENCY_CONFLICT_CODE = 'FORM_CONCURRENCY_CONFLICT';

// Phase 13 coupling audit — extracted out of TaskDetailPage.tsx (was a 502-line file mixing task
// state, this form-instance resolution/save/submit block, and the approval-action UI). Pure
// extraction, no behavior change: still resolves/loads the FormInstance already auto-created by
// WorkflowTransitions.CreateTaskForNodeAsync (Phase 4) and only ever calls Save Draft / Submit on
// it — task completion for a form-bound UserTask still happens server-side via
// FormEngine.SubmitAsync -> WorkflowTransitions, never called directly from here.
export function TaskFormRuntime({ formInstanceId, readOnly, onDirtyChange }: { formInstanceId: string; readOnly: boolean; onDirtyChange: (dirty: boolean) => void }) {
  const { t } = useTranslation();
  const instanceQuery = useFormInstance(formInstanceId);
  const dataQuery = useFormInstanceData(formInstanceId);
  const schemaQuery = useFormInstanceSchema(instanceQuery.data?.formDefinitionId, instanceQuery.data?.formVersionId);
  const saveMutation = useSaveFormInstanceData(formInstanceId);
  const submitMutation = useSubmitFormInstance(formInstanceId);

  const [expectedVersion, setExpectedVersion] = useState<string | null>(null);
  const [liveData, setLiveData] = useState<Record<string, unknown>>({});
  const [dirty, setDirty] = useState(false);
  const [conflicted, setConflicted] = useState(false);
  const [renderKey, setRenderKey] = useState(0);
  const [backendResult, setBackendResult] = useState<WorkflowValidationResult | null>(null);

  useEffect(() => {
    if (dataQuery.data) {
      setExpectedVersion(dataQuery.data.version);
      setLiveData(dataQuery.data.data);
      setRenderKey((k) => k + 1);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dataQuery.data?.version]);

  useEffect(() => {
    function handler(e: BeforeUnloadEvent) {
      if (dirty) {
        e.preventDefault();
        e.returnValue = '';
      }
    }
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [dirty]);

  const isLoading = instanceQuery.isLoading || dataQuery.isLoading || schemaQuery.isLoading;
  const error = instanceQuery.error || dataQuery.error || schemaQuery.error;

  async function handleReload() {
    const result = await dataQuery.refetch();
    if (result.data) {
      setExpectedVersion(result.data.version);
      setLiveData(result.data.data);
      setConflicted(false);
      setBackendResult(null);
      setRenderKey((k) => k + 1);
      message.info(t('tasks.reloadedMsg'));
    }
  }

  function handleSaveDraft() {
    if (!expectedVersion) return;
    saveMutation.mutate(
      { data: liveData, expectedVersion },
      {
        onSuccess: (saved) => {
          setExpectedVersion(saved.version);
          setConflicted(false);
          setBackendResult(null);
          message.success(t('tasks.draftSavedMsg'));
        },
        onError: (err) => {
          if (toApiError(err).code === FORM_CONCURRENCY_CONFLICT_CODE) {
            setConflicted(true);
          } else {
            const api = toApiError(err);
            setBackendResult({ isValid: false, errors: api.errors ?? [{ code: api.code, message: api.message }] });
          }
        },
      },
    );
  }

  function handleSubmit() {
    if (!expectedVersion) return;
    // Submit always reflects the latest edited data — save first if there's anything unsaved, then
    // submit the now-current instance.
    const doSubmit = () => {
      submitMutation.mutate(undefined, {
        onSuccess: () => message.success(t('tasks.submittedMsg')),
        onError: (err) => {
          const api = toApiError(err);
          setBackendResult({ isValid: false, errors: api.errors ?? [{ code: api.code, message: api.message }] });
        },
      });
    };

    if (dirty) {
      saveMutation.mutate(
        { data: liveData, expectedVersion },
        {
          onSuccess: (saved) => {
            setExpectedVersion(saved.version);
            setConflicted(false);
            doSubmit();
          },
          onError: (err) => {
            if (toApiError(err).code === FORM_CONCURRENCY_CONFLICT_CODE) {
              setConflicted(true);
            } else {
              const api = toApiError(err);
              setBackendResult({ isValid: false, errors: api.errors ?? [{ code: api.code, message: api.message }] });
            }
          },
        },
      );
    } else {
      doSubmit();
    }
  }

  return (
    <QueryStateView isLoading={isLoading} error={error}>
      {conflicted && (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('tasks.formConflictTitle')}
          description={t('tasks.formConflictDescription')}
          action={
            <Button size="small" danger onClick={handleReload}>
              {t('tasks.reloadLatest')}
            </Button>
          }
        />
      )}
      {schemaQuery.version && (
        <FormRuntime
          key={renderKey}
          schema={schemaQuery.version.schema}
          formInstanceId={formInstanceId}
          initialData={liveData}
          readOnly={readOnly}
          onChange={(data, isDirty) => {
            setLiveData(data);
            setDirty(isDirty);
            onDirtyChange(isDirty);
          }}
          onSaveDraft={handleSaveDraft}
          isSaving={saveMutation.isPending}
          onSubmit={handleSubmit}
          isSubmitting={submitMutation.isPending}
          backendResult={backendResult}
        />
      )}
    </QueryStateView>
  );
}
