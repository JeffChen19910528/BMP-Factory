import { Modal, Typography } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { FormSchema } from '../../types/form';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateFormVersion } from './hooks';

const STARTER_SCHEMA: FormSchema = {
  fields: [{ key: 'field1', type: 'Text', label: 'Untitled Field', required: false }],
};

interface CreateFormVersionModalProps {
  open: boolean;
  formDefinitionId: string;
  onClose: () => void;
}

// Creates a new Draft version from a minimal one-field starter schema, then hands off to the Form
// Designer to build it out — mirrors CreateVersionModal's process-side pattern exactly.
export function CreateFormVersionModal({ open, formDefinitionId, onClose }: CreateFormVersionModalProps) {
  const navigate = useNavigate();
  const mutation = useCreateFormVersion(formDefinitionId);
  const { t } = useTranslation();

  function handleOk() {
    mutation.mutate(
      { schema: STARTER_SCHEMA },
      {
        onSuccess: (version) => {
          onClose();
          navigate(`/forms/${formDefinitionId}/versions/${version.id}`);
        },
      },
    );
  }

  return (
    <Modal
      title={t('forms.createDraftVersion')}
      open={open}
      onCancel={onClose}
      onOk={handleOk}
      confirmLoading={mutation.isPending}
      okText={t('forms.createDraft')}
    >
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('forms.createDraftVersionFailed')} />}
      <Typography.Paragraph>{t('forms.createDraftVersionDescription')}</Typography.Paragraph>
    </Modal>
  );
}
