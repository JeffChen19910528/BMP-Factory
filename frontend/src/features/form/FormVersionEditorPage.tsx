import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Card, Modal, Segmented, Space, Tag, message } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation } from '@tanstack/react-query';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { QueryStateView } from '../../components/QueryStateView';
import { toApiError } from '../../services/apiClient';
import { validateFormSchema } from '../../services/formDefinitionService';
import type { FormSchema, WorkflowValidationResult } from '../../types/form';
import { useTranslation } from '../../i18n/LanguageContext';
import { useFormVersions, usePublishFormVersion, useUpdateFormVersion, useFormDefinition } from './hooks';
import { FormDesigner } from './designer/FormDesigner';

type EditorMode = 'designer' | 'json';

const CONCURRENCY_CONFLICT_CODE = 'FORM_VERSION_CONCURRENCY_CONFLICT';

// Mirrors ProcessVersionEditorPage.tsx exactly (frontend spec §3/§14): the visual Form Designer
// is the default way to edit a Draft FormVersion's SchemaJson; a JSON "Advanced" mode is kept
// alongside it (still the only way to touch a field type the designer doesn't understand, or a
// Visibility rule it doesn't expose UI for). Both modes edit the exact same FormSchema JSON — no
// second, designer-only schema.
export function FormVersionEditorPage() {
  const { id, versionId } = useParams<{ id: string; versionId: string }>();
  const navigate = useNavigate();
  const { t } = useTranslation();

  const definitionQuery = useFormDefinition(id);
  const versionsQuery = useFormVersions(id);
  const updateMutation = useUpdateFormVersion(id ?? '');
  const publishMutation = usePublishFormVersion(id ?? '');
  const validateMutation = useMutation({ mutationFn: validateFormSchema });

  const version = versionsQuery.data?.find((v) => v.id === versionId);
  const isDraft = version?.status === 'Draft';

  const [mode, setMode] = useState<EditorMode>('designer');
  const [designerKey, setDesignerKey] = useState(0);
  const [loadedVersionId, setLoadedVersionId] = useState<string | null>(null);

  const [baselineSchema, setBaselineSchema] = useState<FormSchema | null>(null);
  const [expectedVersion, setExpectedVersion] = useState<string | null>(null);
  const [liveSchema, setLiveSchema] = useState<FormSchema | null>(null);
  const [designerDirty, setDesignerDirty] = useState(false);

  const [jsonText, setJsonText] = useState('');
  const [jsonSyntaxError, setJsonSyntaxError] = useState<string | null>(null);

  const [validationResult, setValidationResult] = useState<WorkflowValidationResult | null>(null);

  // Same "safe reload/recovery path" concurrency contract as ProcessVersionEditorPage — see
  // PROGRESS.md's Phase 5.4.1 section for why FormVersion needed the identical fix
  // ProcessVersion got in Phase 5.3.2 (no concurrency check existed at all before this phase).
  const [conflicted, setConflicted] = useState(false);

  function loadFromVersion(v: NonNullable<typeof version>) {
    setBaselineSchema(v.schema);
    setExpectedVersion(v.rowVersion);
    setLiveSchema(v.schema);
    setJsonText(JSON.stringify(v.schema, null, 2));
    setDesignerDirty(false);
    setJsonSyntaxError(null);
    setValidationResult(null);
    setConflicted(false);
    setDesignerKey((k) => k + 1);
    setMode('designer');
  }

  useEffect(() => {
    if (version && loadedVersionId !== version.id) {
      loadFromVersion(version);
      setLoadedVersionId(version.id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version, loadedVersionId]);

  const savedJsonText = useMemo(() => (baselineSchema ? JSON.stringify(baselineSchema, null, 2) : ''), [baselineSchema]);
  const jsonDirty = jsonText !== savedJsonText;
  const dirty = mode === 'designer' ? designerDirty : jsonDirty;

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

  function guardedNavigateAway(target: string) {
    if (!dirty) {
      navigate(target);
      return;
    }
    Modal.confirm({
      title: t('forms.discardChangesTitle'),
      content: t('forms.discardChangesContent'),
      okText: t('forms.discardAndLeave'),
      okButtonProps: { danger: true },
      cancelText: t('forms.stay'),
      onOk: () => navigate(target),
    });
  }

  function switchToJson() {
    if (liveSchema) setJsonText(JSON.stringify(liveSchema, null, 2));
    setMode('json');
  }

  function switchToDesigner() {
    try {
      const parsed = JSON.parse(jsonText) as FormSchema;
      setLiveSchema(parsed);
      setJsonSyntaxError(null);
      setDesignerKey((k) => k + 1);
      setMode('designer');
    } catch (e) {
      setJsonSyntaxError(e instanceof Error ? e.message : 'Invalid JSON');
    }
  }

  function getCurrentSchemaOrNull(): FormSchema | null {
    if (mode === 'designer') return liveSchema;
    try {
      const parsed = JSON.parse(jsonText) as FormSchema;
      setJsonSyntaxError(null);
      return parsed;
    } catch (e) {
      setJsonSyntaxError(e instanceof Error ? e.message : 'Invalid JSON');
      return null;
    }
  }

  async function handleReload() {
    const result = await versionsQuery.refetch();
    const fresh = result.data?.find((v) => v.id === versionId);
    if (fresh) {
      loadFromVersion(fresh);
      message.info(t('forms.reloadedLatestVersion'));
    }
  }

  function handleValidate() {
    const schema = getCurrentSchemaOrNull();
    if (!schema) return;
    validateMutation.mutate(schema, { onSuccess: setValidationResult });
  }

  function handleSaveDraft() {
    const schema = getCurrentSchemaOrNull();
    if (!schema || !expectedVersion) return;
    updateMutation.mutate(
      { versionId: versionId!, request: { schema, expectedVersion } },
      {
        onSuccess: (saved) => {
          setBaselineSchema(saved.schema);
          setExpectedVersion(saved.rowVersion);
          setLiveSchema(saved.schema);
          setJsonText(JSON.stringify(saved.schema, null, 2));
          setDesignerDirty(false);
          setConflicted(false);
          message.success(t('forms.draftSaved'));
        },
        onError: (error) => {
          if (toApiError(error).code === CONCURRENCY_CONFLICT_CODE) setConflicted(true);
        },
      },
    );
  }

  function handlePublish() {
    const schema = getCurrentSchemaOrNull();
    if (!schema || !expectedVersion) return;

    const doPublish = () => {
      publishMutation.mutate(undefined, {
        onSuccess: () => message.success(t('forms.publishedSuccess')),
      });
    };

    if (dirty) {
      updateMutation.mutate(
        { versionId: versionId!, request: { schema, expectedVersion } },
        {
          onSuccess: (saved) => {
            setBaselineSchema(saved.schema);
            setExpectedVersion(saved.rowVersion);
            setLiveSchema(saved.schema);
            setJsonText(JSON.stringify(saved.schema, null, 2));
            setDesignerDirty(false);
            setConflicted(false);
            doPublish();
          },
          onError: (error) => {
            if (toApiError(error).code === CONCURRENCY_CONFLICT_CODE) setConflicted(true);
          },
        },
      );
    } else {
      doPublish();
    }
  }

  const isReadOnly = !isDraft;

  return (
    <QueryStateView isLoading={versionsQuery.isLoading} error={versionsQuery.error} isEmpty={!version} emptyDescription={t('forms.versionNotFound')}>
      {version && liveSchema && (
        <Card
          title={
            <Space>
              <span>{t('forms.version')} {version.versionNumber} {t('forms.schema')}</span>
              <Tag color={isDraft ? 'default' : 'green'}>{version.status}</Tag>
            </Space>
          }
          extra={
            <Space>
              <Segmented<EditorMode>
                value={mode}
                onChange={(next) => (next === 'json' ? switchToJson() : switchToDesigner())}
                options={[
                  { label: t('forms.visualDesigner'), value: 'designer' },
                  { label: t('forms.advancedJson'), value: 'json' },
                ]}
              />
              <Button onClick={() => guardedNavigateAway(`/forms/${id}`)}>{t('forms.backToForm')}</Button>
            </Space>
          }
        >
          {!isDraft && (
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('forms.publishedReadOnlyNotice')}
            />
          )}

          {conflicted && (
            <Alert
              type="error"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('forms.draftChangedElsewhereTitle')}
              description={t('forms.draftChangedElsewhereDescription')}
              action={
                <Button size="small" danger onClick={handleReload}>
                  {t('forms.reload')}
                </Button>
              }
            />
          )}

          {jsonSyntaxError && (
            <Alert type="error" showIcon style={{ marginBottom: 16 }} message={t('forms.invalidJsonSyntax')} description={jsonSyntaxError} />
          )}

          {updateMutation.isError && toApiError(updateMutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
            <ApiErrorAlert error={updateMutation.error} title={t('forms.saveFailed')} />
          )}
          {publishMutation.isError && toApiError(publishMutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
            <ApiErrorAlert error={publishMutation.error} title={t('forms.publishFailed')} />
          )}
          {validateMutation.isError && <ApiErrorAlert error={validateMutation.error} title={t('forms.validationRequestFailed')} />}

          {mode === 'designer' ? (
            <FormDesigner
              key={designerKey}
              initialSchema={liveSchema}
              readOnly={isReadOnly}
              formName={definitionQuery.data?.name}
              onChange={(schema, isDirty) => {
                setLiveSchema(schema);
                setDesignerDirty(isDirty);
              }}
              onSaveDraft={handleSaveDraft}
              isSaving={updateMutation.isPending}
              onValidate={handleValidate}
              isValidating={validateMutation.isPending}
              validationResult={validationResult}
              onPublish={handlePublish}
              isPublishing={publishMutation.isPending}
            />
          ) : (
            <>
              {!isReadOnly && (
                <Space style={{ marginBottom: 12 }}>
                  <Button onClick={handleValidate} loading={validateMutation.isPending}>
                    {t('processes.validate')}
                  </Button>
                  <Button type="primary" onClick={handleSaveDraft} loading={updateMutation.isPending}>
                    {t('common.saveDraft')}
                  </Button>
                  <Button onClick={handlePublish} loading={publishMutation.isPending}>
                    {t('common.publish')}
                  </Button>
                  {jsonDirty && <Tag color="gold">{t('forms.unsavedChanges')}</Tag>}
                </Space>
              )}
              {validationResult && !jsonSyntaxError && (
                <Alert
                  style={{ marginBottom: 16 }}
                  type={validationResult.isValid ? 'success' : 'warning'}
                  showIcon
                  message={validationResult.isValid ? t('forms.schemaValid') : t('forms.schemaInvalid')}
                  description={
                    validationResult.isValid ? undefined : (
                      <ul style={{ margin: 0, paddingInlineStart: 20 }}>
                        {validationResult.errors.map((e, i) => (
                          <li key={i}>
                            <code>{e.code}</code> {e.message}
                          </li>
                        ))}
                      </ul>
                    )
                  }
                />
              )}
              <textarea
                value={jsonText}
                onChange={(e) => setJsonText(e.target.value)}
                readOnly={isReadOnly}
                spellCheck={false}
                style={{
                  width: '100%',
                  minHeight: 480,
                  fontFamily: 'ui-monospace, Consolas, monospace',
                  fontSize: 13,
                  padding: 12,
                  boxSizing: 'border-box',
                  border: '1px solid #d9d9d9',
                  borderRadius: 6,
                  background: isReadOnly ? '#fafafa' : '#fff',
                  resize: 'vertical',
                }}
              />
            </>
          )}
        </Card>
      )}
    </QueryStateView>
  );
}
