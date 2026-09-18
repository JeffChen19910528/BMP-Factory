import { Modal, Typography } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { WorkflowDefinition } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateProcessVersion } from './hooks';

const STARTER_DEFINITION: WorkflowDefinition = {
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [],
};

interface CreateVersionModalProps {
  open: boolean;
  processDefinitionId: string;
  onClose: () => void;
}

// Creates a new Draft version from a minimal Start->End starter graph, then hands off to the JSON
// Definition Editor to fill in — this is intentionally the only place a version is created from,
// keeping the "invent a visual designer" scope boundary (frontend spec §18) clean.
export function CreateVersionModal({ open, processDefinitionId, onClose }: CreateVersionModalProps) {
  const navigate = useNavigate();
  const mutation = useCreateProcessVersion(processDefinitionId);
  const { t } = useTranslation();

  function handleOk() {
    mutation.mutate(
      { definition: STARTER_DEFINITION },
      {
        onSuccess: (version) => {
          onClose();
          navigate(`/processes/${processDefinitionId}/versions/${version.id}`);
        },
      },
    );
  }

  return (
    <Modal
      title={t('processes.createDraftVersionTitle')}
      open={open}
      onCancel={onClose}
      onOk={handleOk}
      confirmLoading={mutation.isPending}
      okText={t('processes.createDraft')}
    >
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('processes.createDraftVersionError')} />}
      <Typography.Paragraph>{t('processes.createDraftVersionDescription')}</Typography.Paragraph>
    </Modal>
  );
}
