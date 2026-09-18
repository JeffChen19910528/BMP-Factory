import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { LanguageProvider, type Language } from '../i18n/LanguageContext';

// Shared test harness: a fresh QueryClient per render (no caching bleed between tests) plus a
// MemoryRouter so components using useNavigate/useParams/Link work without a real browser.
// Phase 13 — also wraps LanguageProvider, since useTranslation() now throws outside one and most
// shared components (QueryStateView, ApiErrorAlert, AppLayout, ...) call it. Defaults to en-US so
// existing tests asserting on English text keep passing unchanged; pass `language: 'zh-TW'` for a
// test that specifically exercises the Traditional Chinese UI.
export function renderWithProviders(
  ui: ReactElement,
  { route = '/', path = '/', language = 'en-US' }: { route?: string; path?: string; language?: Language } = {},
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <LanguageProvider initialLanguage={language}>
        <MemoryRouter initialEntries={[route]}>
          <Routes>
            <Route path={path} element={ui} />
          </Routes>
        </MemoryRouter>
      </LanguageProvider>
    </QueryClientProvider>,
  );
}
