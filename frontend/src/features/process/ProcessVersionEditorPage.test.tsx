import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ProcessVersion } from '../../types/process';
import { ProcessVersionEditorPage } from './ProcessVersionEditorPage';

const { listProcessVersions, updateProcessVersion, publishProcessVersion, validateWorkflowDefinition } = vi.hoisted(() => ({
  listProcessVersions: vi.fn(),
  updateProcessVersion: vi.fn(),
  publishProcessVersion: vi.fn(),
  validateWorkflowDefinition: vi.fn(),
}));

vi.mock('../../services/processService', () => ({
  listProcessVersions,
  updateProcessVersion,
  publishProcessVersion,
  validateWorkflowDefinition,
}));

function version(overrides: Partial<ProcessVersion> = {}): ProcessVersion {
  return {
    id: 'v1',
    processDefinitionId: 'proc-1',
    versionNumber: 1,
    status: 'Draft',
    definition: {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [],
    },
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    publishedAt: null,
    publishedBy: null,
    changeReason: null,
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
});

describe('ProcessVersionEditorPage — Visual Designer mode (default)', () => {
  it('renders the designer with the palette and loaded nodes', async () => {
    listProcessVersions.mockResolvedValue([version()]);

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    expect(await screen.findByRole('button', { name: 'Start' })).toBeInTheDocument();
    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(2));
  });

  it('renders a published version read-only, with no palette or Save/Validate/Publish', async () => {
    listProcessVersions.mockResolvedValue([version({ status: 'Published', publishedAt: '2026-01-02T00:00:00Z' })]);

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(2));
    expect(screen.queryByRole('button', { name: 'Start' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Draft' })).not.toBeInTheDocument();
    expect(screen.getByText(/published and immutable/i)).toBeInTheDocument();
  });

  it('saves the current graph via the existing Save Draft API', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    updateProcessVersion.mockResolvedValue(version());
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'End' }));
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    await waitFor(() => expect(updateProcessVersion).toHaveBeenCalledWith('proc-1', 'v1', expect.anything()));
    const [, , request] = updateProcessVersion.mock.calls[0];
    expect(request.definition.nodes).toHaveLength(3);
  });

  it('publishes by saving first when dirty, then calling the publish API', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    updateProcessVersion.mockResolvedValue(version());
    publishProcessVersion.mockResolvedValue(version({ status: 'Published' }));
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'End' }));
    await waitFor(() => expect(screen.getByText('Unsaved changes')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(updateProcessVersion).toHaveBeenCalled());
    await waitFor(() => expect(publishProcessVersion).toHaveBeenCalledWith('proc-1', undefined));
  });

  it('publishes directly (no save call) when the graph has no unsaved changes', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    publishProcessVersion.mockResolvedValue(version({ status: 'Published' }));
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await screen.findByRole('button', { name: 'Publish' });
    await user.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(publishProcessVersion).toHaveBeenCalledWith('proc-1', undefined));
    expect(updateProcessVersion).not.toHaveBeenCalled();
  });
});

describe('ProcessVersionEditorPage — Advanced (JSON) mode', () => {
  it('switches to JSON mode showing the same definition the designer loaded', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await screen.findByRole('button', { name: 'Start' });
    const textarea = await switchToJsonMode(user);
    await waitFor(() => expect((textarea as HTMLTextAreaElement).value).toContain('"start"'));
    expect(textarea).not.toHaveAttribute('readonly');
  });

  it('blocks Validate/Save on invalid JSON syntax without calling the backend', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.clear(textarea);
    await user.type(textarea, '{{not valid json');

    await user.click(screen.getByRole('button', { name: 'Validate' }));
    expect(await screen.findByText(/Invalid JSON syntax/i)).toBeInTheDocument();
    expect(validateWorkflowDefinition).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Save Draft' }));
    expect(updateProcessVersion).not.toHaveBeenCalled();
  });

  it('refuses to switch back to the designer while the JSON is invalid', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.clear(textarea);
    await user.type(textarea, 'not json at all');

    await user.click(screen.getByText('Visual Designer'));

    expect(await screen.findByText(/Invalid JSON syntax/i)).toBeInTheDocument();
    // Still in JSON mode — the textbox is still showing, not the designer's palette.
    expect(screen.getByRole('textbox')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Start' })).not.toBeInTheDocument();
  });

  it('shows structured validation errors from the backend', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    validateWorkflowDefinition.mockResolvedValue({
      isValid: false,
      errors: [{ code: 'MISSING_ASSIGNMENT', message: 'UserTask node needs an assignment.' }],
    });
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await switchToJsonMode(user);
    await user.click(screen.getByRole('button', { name: 'Validate' }));

    expect(await screen.findByText('Definition failed validation')).toBeInTheDocument();
    expect(screen.getByText('MISSING_ASSIGNMENT')).toBeInTheDocument();
    expect(screen.getByText('UserTask node needs an assignment.')).toBeInTheDocument();
  });

  it('saves the draft and clears the unsaved-changes indicator', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    updateProcessVersion.mockResolvedValue(version());
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.type(textarea, ' ');
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    await waitFor(() => expect(updateProcessVersion).toHaveBeenCalledWith('proc-1', 'v1', expect.anything()));
    await waitFor(() => expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument());
  });

  it('warns before navigating back to the process with unsaved changes', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    const textarea = await switchToJsonMode(user);
    await user.type(textarea, ' ');

    await user.click(screen.getByRole('button', { name: 'Back to Process' }));
    expect(await screen.findByRole('button', { name: 'Discard and leave' })).toBeInTheDocument();
  });
});

const CONFLICT_ERROR = {
  isAxiosError: true,
  response: {
    status: 409,
    data: { code: 'PROCESS_VERSION_CONCURRENCY_CONFLICT', message: 'This draft was modified by another request. Reload and retry.', traceId: 't-1' },
  },
};

describe('ProcessVersionEditorPage — 409 concurrency conflict', () => {
  it('shows a dedicated conflict banner instead of silently overwriting, and offers Reload', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    updateProcessVersion.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'End' }));
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    expect(await screen.findByText('This draft was changed by someone else')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Reload' })).toBeInTheDocument();
    // The conflict gets its own dedicated banner — it must not also show the generic
    // "Save failed" alert, which would be confusing/redundant for the same event.
    expect(screen.queryByText('Save failed')).not.toBeInTheDocument();
  });

  it('Reload discards local edits and loads whatever is actually saved on the server', async () => {
    const serverVersionAfterOtherUsersSave = version({
      definition: {
        nodes: [
          { id: 'start', type: 'Start', name: 'Start' },
          { id: 'approval', type: 'ApprovalTask', name: 'Someone else added this', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: 'Manager' }] } },
          { id: 'end', type: 'End', name: 'End' },
        ],
        transitions: [
          { id: 't1', source: 'start', target: 'approval' },
          { id: 't2', source: 'approval', target: 'end' },
        ],
      },
      rowVersion: 'AAAAAAAAAAI=',
    });

    listProcessVersions
      .mockResolvedValueOnce([version()])
      .mockResolvedValue([serverVersionAfterOtherUsersSave]);
    updateProcessVersion.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'End' })); // now dirty, 3 nodes locally
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));
    await screen.findByRole('button', { name: 'Reload' });

    await user.click(screen.getByRole('button', { name: 'Reload' }));

    // Reload replaced the local (conflicting) graph with the server's actual current state.
    await waitFor(() => expect(screen.getByText('Someone else added this')).toBeInTheDocument());
    expect(screen.queryByText('This draft was changed by someone else')).not.toBeInTheDocument();
  });

  it('publish also detects a stale write and does not proceed to actually publish', async () => {
    listProcessVersions.mockResolvedValue([version()]);
    updateProcessVersion.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<ProcessVersionEditorPage />, {
      route: '/processes/proc-1/versions/v1',
      path: '/processes/:id/versions/:versionId',
    });

    await user.click(await screen.findByRole('button', { name: 'End' }));
    await user.click(screen.getByRole('button', { name: 'Publish' }));

    expect(await screen.findByText('This draft was changed by someone else')).toBeInTheDocument();
    expect(publishProcessVersion).not.toHaveBeenCalled();
  });
});
