import { Button, Space, Typography } from 'antd';
import type { SUPPORTED_NODE_TYPES } from './graphModel';
import { useTranslation } from '../../../i18n/LanguageContext';

type PaletteNodeType = (typeof SUPPORTED_NODE_TYPES)[number];

interface NodePaletteProps {
  onAddNode: (nodeType: PaletteNodeType) => void;
  disabled?: boolean;
}

// Drag-and-drop onto the canvas is the primary interaction (standard React Flow palette
// pattern), but every item is also a plain click target (frontend spec §6: "Add node through
// click if practical") — click is what the automated tests drive, since simulating native HTML5
// drag events in jsdom is unreliable.
export function NodePalette({ onAddNode, disabled }: NodePaletteProps) {
  const { t } = useTranslation();

  const PALETTE_ITEMS: { type: PaletteNodeType; label: string }[] = [
    { type: 'Start', label: t('processDesigner.nodeStart') },
    { type: 'UserTask', label: t('processDesigner.nodeUserTask') },
    { type: 'ApprovalTask', label: t('processDesigner.nodeApprovalTask') },
    { type: 'End', label: t('processDesigner.nodeEnd') },
  ];

  function handleDragStart(event: React.DragEvent, nodeType: PaletteNodeType) {
    event.dataTransfer.setData('application/bpm-node-type', nodeType);
    event.dataTransfer.effectAllowed = 'move';
  }

  return (
    <div style={{ padding: 12 }}>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('processDesigner.paletteHint')}
      </Typography.Text>
      <Space direction="vertical" style={{ width: '100%', marginTop: 8 }}>
        {PALETTE_ITEMS.map((item) => (
          <Button
            key={item.type}
            block
            draggable
            disabled={disabled}
            onDragStart={(e) => handleDragStart(e, item.type)}
            onClick={() => onAddNode(item.type)}
            style={{ cursor: 'grab', textAlign: 'left' }}
          >
            {item.label}
          </Button>
        ))}
      </Space>
    </div>
  );
}
