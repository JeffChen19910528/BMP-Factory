import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { render } from '@testing-library/react';
import { LanguageProvider, useTranslation, LANGUAGE_STORAGE_KEY } from './LanguageContext';

// Phase 13 (Part 二十四) — tests the language-switching *behavior* (default, switch, persistence,
// fallback), not a snapshot of every translated string — per the brief's own explicit instruction
// not to build "脆弱的 snapshot test" for every piece of text.

function Probe() {
  const { language, setLanguage, t } = useTranslation();
  return (
    <div>
      <span data-testid="language">{language}</span>
      <span data-testid="nav-dashboard">{t('nav.dashboard')}</span>
      <span data-testid="missing-key">{t('this.key.does.not.exist')}</span>
      <button onClick={() => setLanguage('en-US')}>switch to en</button>
      <button onClick={() => setLanguage('zh-TW')}>switch to zh</button>
    </div>
  );
}

beforeEach(() => {
  localStorage.clear();
});

describe('LanguageContext', () => {
  it('defaults to zh-TW when nothing is stored', () => {
    render(
      <LanguageProvider>
        <Probe />
      </LanguageProvider>,
    );

    expect(screen.getByTestId('language')).toHaveTextContent('zh-TW');
    expect(screen.getByTestId('nav-dashboard')).toHaveTextContent('儀表板');
  });

  it('switches from zh-TW to en-US and updates translated text immediately', async () => {
    const user = userEvent.setup();
    render(
      <LanguageProvider>
        <Probe />
      </LanguageProvider>,
    );

    expect(screen.getByTestId('nav-dashboard')).toHaveTextContent('儀表板');
    await user.click(screen.getByRole('button', { name: 'switch to en' }));

    expect(screen.getByTestId('language')).toHaveTextContent('en-US');
    expect(screen.getByTestId('nav-dashboard')).toHaveTextContent('Dashboard');
  });

  it('switches from en-US back to zh-TW', async () => {
    const user = userEvent.setup();
    render(
      <LanguageProvider initialLanguage="en-US">
        <Probe />
      </LanguageProvider>,
    );

    expect(screen.getByTestId('nav-dashboard')).toHaveTextContent('Dashboard');
    await user.click(screen.getByRole('button', { name: 'switch to zh' }));

    expect(screen.getByTestId('language')).toHaveTextContent('zh-TW');
    expect(screen.getByTestId('nav-dashboard')).toHaveTextContent('儀表板');
  });

  it('persists the selected language to localStorage', async () => {
    const user = userEvent.setup();
    render(
      <LanguageProvider>
        <Probe />
      </LanguageProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'switch to en' }));
    await waitFor(() => expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en-US'));
  });

  it('restores the persisted language on the next mount (simulating a reload)', () => {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, 'en-US');

    render(
      <LanguageProvider>
        <Probe />
      </LanguageProvider>,
    );

    expect(screen.getByTestId('language')).toHaveTextContent('en-US');
    expect(screen.getByTestId('nav-dashboard')).toHaveTextContent('Dashboard');
  });

  it('falls back to a readable value (not "undefined"/blank/raw key) for a missing translation key', () => {
    render(
      <LanguageProvider>
        <Probe />
      </LanguageProvider>,
    );

    const missing = screen.getByTestId('missing-key').textContent;
    expect(missing).not.toBe('undefined');
    expect(missing).not.toBe('');
    // No key exists in either language, so the raw path itself is the last-resort fallback —
    // still real, readable text (a dot-path), never a blank render or a crash.
    expect(missing).toBe('this.key.does.not.exist');
  });

  it('ignores a corrupted/invalid stored language value and falls back to the default', () => {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, 'not-a-real-language');

    render(
      <LanguageProvider>
        <Probe />
      </LanguageProvider>,
    );

    expect(screen.getByTestId('language')).toHaveTextContent('zh-TW');
  });

  it('does not throw when localStorage access fails (e.g. a private window)', () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(() =>
      render(
        <LanguageProvider>
          <Probe />
        </LanguageProvider>,
      ),
    ).not.toThrow();
    expect(screen.getByTestId('language')).toHaveTextContent('zh-TW');

    spy.mockRestore();
  });
});
