import { screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { FormDefinition, FormVersion } from '../../types/form';
import { FormDetailPage } from './FormDetailPage';

const { getFormDefinition, listFormVersions, publishFormVersion } = vi.hoisted(() => ({
  getFormDefinition: vi.fn(),
  listFormVersions: vi.fn(),
  publishFormVersion: vi.fn(),
}));

vi.mock('../../services/formDefinitionService', () => ({
  getFormDefinition,
  listFormVersions,
  publishFormVersion,
  updateFormDefinition: vi.fn(),
  createFormVersion: vi.fn(),
  updateFormVersion: vi.fn(),
}));

vi.mock('../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([]),
}));

function baseDefinition(overrides: Partial<FormDefinition> = {}): FormDefinition {
  return {
    id: 'form-1',
    key: 'purchase-request',
    name: 'Purchase Request',
    description: 'A purchase request form',
    category: 'Procurement',
    status: 'Draft',
    currentVersionId: null,
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    updatedAt: null,
    ...overrides,
  };
}

function draftVersion(overrides: Partial<FormVersion> = {}): FormVersion {
  return {
    id: 'v1',
    formDefinitionId: 'form-1',
    versionNumber: 1,
    status: 'Draft',
    schema: { fields: [{ key: 'itemName', type: 'Text', label: 'Item Name', required: true }] },
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    publishedAt: null,
    publishedBy: null,
    rowVersion: 'AAAAAAAAAAE=',
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('FormDetailPage', () => {
  it('shows an error state when the form is not found', async () => {
    getFormDefinition.mockRejectedValue({ isAxiosError: true, response: { status: 404, data: undefined } });
    listFormVersions.mockResolvedValue([]);

    renderWithProviders(<FormDetailPage />, { route: '/forms/missing', path: '/forms/:id' });

    await waitFor(() => expect(screen.getByText(/requested resource was not found/i)).toBeInTheDocument());
  });

  it('enables Create Version and disables Publish when there is no draft', async () => {
    getFormDefinition.mockResolvedValue(baseDefinition());
    listFormVersions.mockResolvedValue([]);

    renderWithProviders(<FormDetailPage />, { route: '/forms/form-1', path: '/forms/:id' });

    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Create Version' })).not.toBeDisabled();
    expect(screen.getByRole('button', { name: 'Publish' })).toBeDisabled();
    expect(screen.getByText(/No versions yet/i)).toBeInTheDocument();
  });

  it('disables Create Version and enables Publish when a draft version already exists, and shows the field count', async () => {
    getFormDefinition.mockResolvedValue(baseDefinition());
    listFormVersions.mockResolvedValue([draftVersion()]);

    renderWithProviders(<FormDetailPage />, { route: '/forms/form-1', path: '/forms/:id' });

    await waitFor(() => expect(screen.getByText('v1')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Create Version' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Publish' })).not.toBeDisabled();
    expect(screen.getByText(/unpublished draft version/i)).toBeInTheDocument();
    expect(screen.getByText('1')).toBeInTheDocument();
  });

  it('shows a published version as read-only ("View" instead of "Edit")', async () => {
    getFormDefinition.mockResolvedValue(baseDefinition({ status: 'Published', currentVersionId: 'v1' }));
    listFormVersions.mockResolvedValue([draftVersion({ status: 'Published', publishedAt: '2026-01-02T00:00:00Z', publishedBy: 'user-1' })]);

    renderWithProviders(<FormDetailPage />, { route: '/forms/form-1', path: '/forms/:id' });

    await waitFor(() => expect(screen.getByText('v1')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'View' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Edit' })).toHaveLength(1);
  });
});
