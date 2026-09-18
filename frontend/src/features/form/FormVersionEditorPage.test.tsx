import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { FormVersion } from '../../types/form';
import { FormVersionEditorPage } from './FormVersionEditorPage';

const { listFormVersions, updateFormVersion, publishFormVersion, validateFormSchema, getFormDefinition } = vi.hoisted(() => ({
  listFormVersions: vi.fn(),
  updateFormVersion: vi.fn(),
  publishFormVersion: vi.fn(),
  validateFormSchema: vi.fn(),
  getFormDefinition: vi.fn(),
}));

vi.mock('../../services/formDefinitionService', () => ({
  listFormVersions,
  updateFormVersion,
  publishFormVersion,
  validateFormSchema,
  getFormDefinition,
}));

function version(overrides: Partial<FormVersion> = {}): FormVersion {
  return {
    id: 'v1',
    formDefinitionId: 'form-1',
    versionNumber: 1,
    status: 'Draft',
    schema: {
      fields: [
        { key: 'itemName', type: 'Text', label: 'Item Name', required: true },
        { key: 'quantity', type: 'Number', label: 'Quantity', required: false },
      ],
    },
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    publishedAt: null,
    publishedBy: null,
    rowVersion: 'AAAAAAAAAAE=',
    ...overrides,
  };
}

async function switchToJsonMode(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByText('Advanced (JSON)'));
  return screen.findByRole('textbox');
}

beforeEach(() => {
  vi.clearAllMocks();
  getFormDefinition.mockResolvedValue({ id: 'form-1', key: 'item-request', name: 'Item Request', description: null, category: null, status: 'Published', currentVersionId: 'v1', createdAt: '2026-01-01T00:00:00Z', createdBy: null, updatedAt: null });
});

describe('FormVersionEditorPage — Visual Designer mode (default)', () => {
  it('renders the designer with the palette and loaded fields', async () => {
    listFormVersions.mockResolvedValue([version()]);

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    expect(await screen.findByRole('button', { name: 'Text' })).toBeInTheDocument();
    expect(screen.getByText('Item Name')).toBeInTheDocument();
    expect(screen.getByText('Quantity')).toBeInTheDocument();
  });

  it('renders a published version read-only, with no palette or Save/Validate/Publish', async () => {
    listFormVersions.mockResolvedValue([version({ status: 'Published', publishedAt: '2026-01-02T00:00:00Z' })]);

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await screen.findByText('Item Name');
    expect(screen.queryByRole('button', { name: 'Text' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Draft' })).not.toBeInTheDocument();
    expect(screen.getByText(/published and immutable/i)).toBeInTheDocument();
  });

  it('saves the current schema via the existing Save Draft API', async () => {
    listFormVersions.mockResolvedValue([version()]);
    updateFormVersion.mockResolvedValue(version());
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'Select' }));
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    await waitFor(() => expect(updateFormVersion).toHaveBeenCalledWith('form-1', 'v1', expect.anything()));
    const [, , request] = updateFormVersion.mock.calls[0];
    expect(request.schema.fields).toHaveLength(3);
  });

  it('publishes by saving first when dirty, then calling the publish API', async () => {
    listFormVersions.mockResolvedValue([version()]);
    updateFormVersion.mockResolvedValue(version());
    publishFormVersion.mockResolvedValue(version({ status: 'Published' }));
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'Select' }));
    await waitFor(() => expect(screen.getByText('Unsaved changes')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(updateFormVersion).toHaveBeenCalled());
    await waitFor(() => expect(publishFormVersion).toHaveBeenCalledWith('form-1'));
  });

  it('publishes directly (no save call) when the schema has no unsaved changes', async () => {
    listFormVersions.mockResolvedValue([version()]);
    publishFormVersion.mockResolvedValue(version({ status: 'Published' }));
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await screen.findByRole('button', { name: 'Publish' });
    await user.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(publishFormVersion).toHaveBeenCalledWith('form-1'));
    expect(updateFormVersion).not.toHaveBeenCalled();
  });
});

describe('FormVersionEditorPage — Advanced (JSON) mode', () => {
  it('switches to JSON mode showing the same schema the designer loaded', async () => {
    listFormVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await screen.findByRole('button', { name: 'Text' });
    const textarea = await switchToJsonMode(user);
    await waitFor(() => expect((textarea as HTMLTextAreaElement).value).toContain('"itemName"'));
    expect(textarea).not.toHaveAttribute('readonly');
  });

  it('blocks Validate/Save on invalid JSON syntax without calling the backend', async () => {
    listFormVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.clear(textarea);
    await user.type(textarea, '{{not valid json');

    await user.click(screen.getByRole('button', { name: 'Validate' }));
    expect(await screen.findByText(/Invalid JSON syntax/i)).toBeInTheDocument();
    expect(validateFormSchema).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Save Draft' }));
    expect(updateFormVersion).not.toHaveBeenCalled();
  });

  it('shows structured validation errors from the backend', async () => {
    listFormVersions.mockResolvedValue([version()]);
    validateFormSchema.mockResolvedValue({
      isValid: false,
      errors: [{ code: 'DUPLICATE_FIELD_KEY', message: "Field key 'quantity' is used more than once." }],
    });
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await switchToJsonMode(user);
    await user.click(screen.getByRole('button', { name: 'Validate' }));

    expect(await screen.findByText('Schema failed validation')).toBeInTheDocument();
    expect(screen.getByText('DUPLICATE_FIELD_KEY')).toBeInTheDocument();
  });

  it('saves the draft and clears the unsaved-changes indicator', async () => {
    listFormVersions.mockResolvedValue([version()]);
    updateFormVersion.mockResolvedValue(version());
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.type(textarea, ' ');
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    await waitFor(() => expect(updateFormVersion).toHaveBeenCalledWith('form-1', 'v1', expect.anything()));
    await waitFor(() => expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument());
  });

  it('warns before navigating back to the form with unsaved changes', async () => {
    listFormVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.type(textarea, ' ');

    await user.click(screen.getByRole('button', { name: 'Back to Form' }));
    expect(await screen.findByRole('button', { name: 'Discard and leave' })).toBeInTheDocument();
  });
});

const CONFLICT_ERROR = {
  isAxiosError: true,
  response: {
    status: 409,
    data: { code: 'FORM_VERSION_CONCURRENCY_CONFLICT', message: 'This draft was modified by another request. Reload and retry.', traceId: 't-1' },
  },
};

describe('FormVersionEditorPage — 409 concurrency conflict', () => {
  it('shows a dedicated conflict banner instead of silently overwriting, and offers Reload', async () => {
    listFormVersions.mockResolvedValue([version()]);
    updateFormVersion.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'Select' }));
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    expect(await screen.findByText('This draft was changed by someone else')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Reload' })).toBeInTheDocument();
    expect(screen.queryByText('Save failed')).not.toBeInTheDocument();
  });

  it('Reload discards local edits and loads whatever is actually saved on the server', async () => {
    const serverVersionAfterOtherUsersSave = version({
      schema: {
        fields: [
          { key: 'itemName', type: 'Text', label: 'Item Name', required: true },
          { key: 'quantity', type: 'Number', label: 'Quantity', required: false },
          { key: 'notes', type: 'Textarea', label: 'Someone else added this', required: false },
        ],
      },
      rowVersion: 'AAAAAAAAAAI=',
    });

    listFormVersions.mockResolvedValueOnce([version()]).mockResolvedValue([serverVersionAfterOtherUsersSave]);
    updateFormVersion.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'Select' }));
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));
    await screen.findByRole('button', { name: 'Reload' });

    await user.click(screen.getByRole('button', { name: 'Reload' }));

    await waitFor(() => expect(screen.getByText('Someone else added this')).toBeInTheDocument());
    expect(screen.queryByText('This draft was changed by someone else')).not.toBeInTheDocument();
  });

  it('publish also detects a stale write and does not proceed to actually publish', async () => {
    listFormVersions.mockResolvedValue([version()]);
    updateFormVersion.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<FormVersionEditorPage />, {
      route: '/forms/form-1/versions/v1',
      path: '/forms/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'Select' }));
    await user.click(screen.getByRole('button', { name: 'Publish' }));

    expect(await screen.findByText('This draft was changed by someone else')).toBeInTheDocument();
    expect(publishFormVersion).not.toHaveBeenCalled();
  });
});
