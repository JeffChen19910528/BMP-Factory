import { useQuery } from '@tanstack/react-query';
import { listFormDefinitions } from '../services/formDefinitionService';

// The workflow engine only ever creates a FormInstance from a *Published* FormDefinition
// (WorkflowEngine.PublishVersionAsync's FORM_REFERENCE_INVALID check requires
// FormDefinitionStatus.Published) — so a Draft/Suspended/Archived form would publish the process
// fine but then fail at runtime with FORM_REFERENCE_INVALID the moment someone tries to publish
// it, or worse, silently mismatch expectations. Filtering to Published here keeps the picker from
// ever offering a choice that can't actually work.
export function usePublishedFormDefinitions() {
  const query = useQuery({ queryKey: ['form-definitions'], queryFn: listFormDefinitions, staleTime: 60 * 1000 });
  const published = (query.data ?? []).filter((f) => f.status === 'Published');
  return { formDefinitions: published, isLoading: query.isLoading, error: query.error };
}
