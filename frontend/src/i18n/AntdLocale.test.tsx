import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfigProvider, DatePicker } from 'antd';
import zhTW from 'antd/locale/zh_TW';
import enUS from 'antd/locale/en_US';
import { describe, expect, it } from 'vitest';
import { useTranslation, LanguageProvider, type Language } from './LanguageContext';

// Phase 13 (Part 二十) — AntD's own component locale (DatePicker/Table/Pagination/Form text) must
// track the selected language, not just our own translation dictionary. Mirrors App.tsx's real
// AppShell wiring (ConfigProvider's `locale` prop keyed off the same language state) in isolation,
// since mounting the full App (real routing/auth) is unnecessary just to prove this one wire-up.
function Harness() {
  const { language, setLanguage } = useTranslation();
  return (
    <ConfigProvider locale={language === 'zh-TW' ? zhTW : enUS}>
      <DatePicker open />
      <button onClick={() => setLanguage(language === 'zh-TW' ? 'en-US' : 'zh-TW')}>toggle</button>
    </ConfigProvider>
  );
}

function renderHarness(initialLanguage: Language) {
  return render(
    <LanguageProvider initialLanguage={initialLanguage}>
      <Harness />
    </LanguageProvider>,
  );
}

describe('AntD component locale follows the selected language', () => {
  it('shows the zh-TW "今天" (Today) label when zh-TW is selected', () => {
    renderHarness('zh-TW');
    expect(screen.getByText('今天')).toBeInTheDocument();
  });

  it('shows the en-US "Today" label when en-US is selected', () => {
    renderHarness('en-US');
    expect(screen.getByText('Today')).toBeInTheDocument();
  });

  it('switches the DatePicker locale live when the language toggle is clicked', async () => {
    const user = userEvent.setup();
    renderHarness('en-US');
    expect(screen.getByText('Today')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'toggle' }));

    expect(await screen.findByText('今天')).toBeInTheDocument();
  });
});
