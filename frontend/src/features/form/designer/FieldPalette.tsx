import { Button, Space, Typography } from 'antd';
import type { FormFieldType } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import { SUPPORTED_FIELD_TYPES } from './formSchemaModel';

interface FieldPaletteProps {
  onAddField: (type: FormFieldType) => void;
  disabled?: boolean;
}

// Mirrors the Process Designer's NodePalette exactly: drag-and-drop onto the canvas is the
// primary interaction, but every item is also a plain click target (frontend spec §6), which is
// what the automated tests drive, since simulating native HTML5 drag events in jsdom is
// unreliable.
export function FieldPalette({ onAddField, disabled }: FieldPaletteProps) {
  const { t } = useTranslation();

  // Display label is translated; the FormFieldType value sent to onAddField/the backend/JSON
  // stays the original English enum value — never localized.
  const paletteItems: { type: FormFieldType; label: string }[] = SUPPORTED_FIELD_TYPES.map((type) => ({
    type,
    label: t(`forms.fieldType.${type}`),
  }));

  function handleDragStart(event: React.DragEvent, type: FormFieldType) {
    event.dataTransfer.setData('application/bpm-field-type', type);
    event.dataTransfer.effectAllowed = 'move';
  }

  return (
    <div style={{ padding: 12 }}>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('forms.dragOrClickToAdd')}
      </Typography.Text>
      <Space direction="vertical" style={{ width: '100%', marginTop: 8 }}>
        {paletteItems.map((item) => (
          <Button
            key={item.type}
            block
            draggable
            disabled={disabled}
            onDragStart={(e) => handleDragStart(e, item.type)}
            onClick={() => onAddField(item.type)}
            style={{ cursor: 'grab', textAlign: 'left' }}
          >
            {item.label}
          </Button>
        ))}
      </Space>
    </div>
  );
}
