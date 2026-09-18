import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Card, Modal, Segmented, Space, Tag, message } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation } from '@tanstack/react-query';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { QueryStateView } from '../../components/QueryStateView';
import { toApiError } from '../../services/apiClient';
import { validateWorkflowDefinition } from '../../services/processService';
import type { WorkflowDefinition, WorkflowValidationResult } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useProcessVersions, usePublishProcessVersion, useUpdateProcessVersion } from './hooks';
import { ProcessDesigner } from './designer/ProcessDesigner';

type EditorMode = 'designer' | 'json';

const CONCURRENCY_CONFLICT_CODE = 'PROCESS_VERSION_CONCURRENCY_CONFLICT';

// Phase 5.3.1: the visual Process Designer (React Flow) is now the default way to edit a Draft
// ProcessVersion's DefinitionJson. The JSON editor from Phase 5.2 §9 is kept as an "Advanced
// (JSON)" mode — per this phase's own instruction ("retain the JSON editor as an optional
// advanced/debug view") — rather than removed outright, since it's still the only way to touch a
// node/property the designer doesn't understand yet (see designer/graphModel.ts's
// SUPPORTED_NODE_TYPES). Both modes edit the exact same WorkflowDefinition JSON; there is no
// second, designer-only schema (frontend spec §9/§11).
export function ProcessVersionEditorPage() {
  const { id, versionId } = useParams<{ id: string; versionId: string }>();
  const navigate = useNavigate();
  const { t } = useTranslation();

  const versionsQuery = useProcessVersions(id);
  const updateMutation = useUpdateProcessVersion(id ?? '');
  const publishMutation = usePublishProcessVersion(id ?? '');
  const validateMutation = useMutation({ mutationFn: validateWorkflowDefinition });

  const version = versionsQuery.data?.find((v) => v.id === versionId);
  const isDraft = version?.status === 'Draft';

  const [mode, setMode] = useState<EditorMode>('designer');
  const [designerKey, setDesignerKey] = useState(0);
  const [loadedVersionId, setLoadedVersionId] = useState<string | null>(null);

  const [baselineDefinition, setBaselineDefinition] = useState<WorkflowDefinition | null>(null);
  const [expectedVersion, setExpectedVersion] = useState<string | null>(null);
  const [liveDefinition, setLiveDefinition] = useState<WorkflowDefinition | null>(null);
  const [designerDirty, setDesignerDirty] = useState(false);

  const [jsonText, setJsonText] = useState('');
  const [jsonSyntaxError, setJsonSyntaxError] = useState<string | null>(null);

  const [validationResult, setValidationResult] = useState<WorkflowValidationResult | null>(null);

  // Phase 5.3.2 §14: two people can open the same Draft at once — Save Draft/Publish must not
  // silently let the second save clobber the first (Skill.md §33's optimistic-locking rule,
  // extended to UpdateVersionAsync, which previously had no concurrency check at all — see
  // PROGRESS.md). `conflicted` means the last save/publish attempt lost that race; the only way
  // out is the explicit "Reload" action below, never a silent retry with stale data.
  const [conflicted, setConflicted] = useState(false);

  function loadFromVersion(v: NonNullable<typeof version>) {
    setBaselineDefinition(v.definition);
    setExpectedVersion(v.rowVersion);
    setLiveDefinition(v.definition);
    setJsonText(JSON.stringify(v.definition, null, 2));
    setDesignerDirty(false);
    setJsonSyntaxError(null);
    setValidationResult(null);
    setConflicted(false);
    setDesignerKey((k) => k + 1);
    setMode('designer');
  }

  // Load exactly once per version (not on every background refetch) so an in-progress edit is
  // never clobbered by a stale server round trip — same rule the Phase 5.2 JSON-only editor used.
  useEffect(() => {
    if (version && loadedVersionId !== version.id) {
      loadFromVersion(version);
      setLoadedVersionId(version.id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version, loadedVersionId]);

  const savedJsonText = useMemo(() => (baselineDefinition ? JSON.stringify(baselineDefinition, null, 2) : ''), [baselineDefinition]);
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
      title: t('processes.discardChangesTitle'),
      content: t('processes.discardChangesContent'),
      okText: t('processes.discardAndLeave'),
      okButtonProps: { danger: true },
      cancelText: t('processes.stay'),
      onOk: () => navigate(target),
    });
  }

  function switchToJson() {
    if (liveDefinition) setJsonText(JSON.stringify(liveDefinition, null, 2));
    setMode('json');
  }

  function switchToDesigner() {
    try {
      const parsed = JSON.parse(jsonText) as WorkflowDefinition;
      setLiveDefinition(parsed);
      setJsonSyntaxError(null);
      setDesignerKey((k) => k + 1);
      setMode('designer');
    } catch (e) {
      setJsonSyntaxError(e instanceof Error ? e.message : 'Invalid JSON');
    }
  }

  // Returns the definition currently "on screen" regardless of which mode is active, or null (and
  // sets the syntax error) if the JSON view holds unparseable text — the one thing Designer mode
  // can't produce, since it always emits well-formed objects.
  function getCurrentDefinitionOrNull(): WorkflowDefinition | null {
    if (mode === 'designer') return liveDefinition;
    try {
      const parsed = JSON.parse(jsonText) as WorkflowDefinition;
      setJsonSyntaxError(null);
      return parsed;
    } catch (e) {
      setJsonSyntaxError(e instanceof Error ? e.message : 'Invalid JSON');
      return null;
    }
  }

  // The safe recovery path after a 409: discard local edits and reload whatever is actually on
  // the server now (a fresh RowVersion included), rather than retrying blind.
  async function handleReload() {
    const result = await versionsQuery.refetch();
    const fresh = result.data?.find((v) => v.id === versionId);
    if (fresh) {
      loadFromVersion(fresh);
      message.info(t('processes.reloadedMessage'));
    }
  }

  function handleValidate() {
    const definition = getCurrentDefinitionOrNull();
    if (!definition) return;
    validateMutation.mutate(definition, { onSuccess: setValidationResult });
  }

  function handleSaveDraft() {
    const definition = getCurrentDefinitionOrNull();
    if (!definition || !expectedVersion) return;
    updateMutation.mutate(
      { versionId: versionId!, request: { definition, expectedVersion } },
      {
        onSuccess: (saved) => {
          setBaselineDefinition(saved.definition);
          setExpectedVersion(saved.rowVersion);
          setLiveDefinition(saved.definition);
          setJsonText(JSON.stringify(saved.definition, null, 2));
          setDesignerDirty(false);
          setConflicted(false);
          message.success(t('processes.draftSavedMessage'));
        },
        onError: (error) => {
          if (toApiError(error).code === CONCURRENCY_CONFLICT_CODE) setConflicted(true);
        },
      },
    );
  }

  function handlePublish() {
    const definition = getCurrentDefinitionOrNull();
    if (!definition || !expectedVersion) return;

    const doPublish = () => {
      publishMutation.mutate(undefined, {
        onSuccess: () => message.success(t('processes.publishedMessage')),
      });
    };

    if (dirty) {
      updateMutation.mutate(
        { versionId: versionId!, request: { definition, expectedVersion } },
        {
          onSuccess: (saved) => {
            setBaselineDefinition(saved.definition);
            setExpectedVersion(saved.rowVersion);
            setLiveDefinition(saved.definition);
            setJsonText(JSON.stringify(saved.definition, null, 2));
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
    <QueryStateView isLoading={versionsQuery.isLoading} error={versionsQuery.error} isEmpty={!version} emptyDescription={t('processes.versionNotFound')}>
      {version && liveDefinition && (
        <Card
          title={
            <Space>
              <span>{t('processes.version')} {version.versionNumber} {t('processes.definitionLabel')}</span>
              <Tag color={isDraft ? 'default' : 'green'}>{version.status}</Tag>
            </Space>
          }
          extra={
            <Space>
              <Segmented<EditorMode>
                value={mode}
                onChange={(next) => (next === 'json' ? switchToJson() : switchToDesigner())}
                options={[
                  { label: t('processes.visualDesigner'), value: 'designer' },
                  { label: t('processes.advancedJson'), value: 'json' },
                ]}
              />
              <Button onClick={() => guardedNavigateAway(`/processes/${id}`)}>{t('processes.backToProcess')}</Button>
            </Space>
          }
        >
          {!isDraft && (
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('processes.publishedReadOnlyAlert')}
            />
          )}

          {conflicted && (
            <Alert
              type="error"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('processes.draftConflictTitle')}
              description={t('processes.draftConflictDescription')}
              action={
                <Button size="small" danger onClick={handleReload}>
                  {t('processes.reload')}
                </Button>
              }
            />
          )}

          {jsonSyntaxError && (
            <Alert
              type="error"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('processes.invalidJsonSyntax')}
              description={jsonSyntaxError}
            />
          )}

          {updateMutation.isError && toApiError(updateMutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
            <ApiErrorAlert error={updateMutation.error} title={t('processes.saveFailed')} />
          )}
          {publishMutation.isError && toApiError(publishMutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
            <ApiErrorAlert error={publishMutation.error} title={t('processes.publishFailed')} />
          )}
          {validateMutation.isError && <ApiErrorAlert error={validateMutation.error} title={t('processes.validationRequestFailed')} />}

          {mode === 'designer' ? (
            <ProcessDesigner
              key={designerKey}
              initialDefinition={liveDefinition}
              readOnly={isReadOnly}
              onChange={(definition, isDirty) => {
                setLiveDefinition(definition);
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
                  {jsonDirty && <Tag color="gold">{t('processes.unsavedChanges')}</Tag>}
                </Space>
              )}
              {validationResult && !jsonSyntaxError && (
                <Alert
                  style={{ marginBottom: 16 }}
                  type={validationResult.isValid ? 'success' : 'warning'}
                  showIcon
                  message={validationResult.isValid ? t('processes.definitionValid') : t('processes.definitionInvalid')}
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
