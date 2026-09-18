import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import zhTW from './locales/zh-TW';
import enUS from './locales/en-US';
import type { TranslationDictionary } from './locales/zh-TW';

export type Language = 'zh-TW' | 'en-US';

export const LANGUAGE_STORAGE_KEY = 'bpm-language';
export const DEFAULT_LANGUAGE: Language = 'zh-TW';

const dictionaries: Record<Language, TranslationDictionary> = {
  'zh-TW': zhTW,
  'en-US': enUS,
};

function isLanguage(value: unknown): value is Language {
  return value === 'zh-TW' || value === 'en-US';
}

// Phase 13 — client-side UI preference only (Part 十五: "不要新增 database column, 不要將語系儲存在
// User entity"). Reads localStorage directly rather than through a runtime capability — this never
// needs to be read back by the backend or shared across devices/viewers.
function readStoredLanguage(): Language {
  try {
    const stored = localStorage.getItem(LANGUAGE_STORAGE_KEY);
    return isLanguage(stored) ? stored : DEFAULT_LANGUAGE;
  } catch {
    // localStorage can throw in a private window or with blocked site data (see Artifact tool's
    // own browser-storage guidance) — fall back to the default rather than crash the app.
    return DEFAULT_LANGUAGE;
  }
}

type TranslateFn = (path: string) => string;

interface LanguageContextValue {
  language: Language;
  setLanguage: (language: Language) => void;
  t: TranslateFn;
  /** Looks up `errors.<code>` without falling back to the raw key — callers (ApiErrorAlert) fall
   * back to the backend's own message instead when no localized text exists for a code. */
  translateErrorCode: (code: string) => string | undefined;
}

const LanguageContext = createContext<LanguageContextValue | null>(null);

function lookup(dict: TranslationDictionary, path: string): string | undefined {
  const segments = path.split('.');
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  let node: any = dict;
  for (const segment of segments) {
    if (node === undefined || node === null) return undefined;
    node = node[segment];
  }
  return typeof node === 'string' ? node : undefined;
}

interface LanguageProviderProps {
  children: ReactNode;
  /** Test-only override — skips localStorage and starts at a fixed language, so component tests
   * that assert on specific translated text don't depend on the default/persisted language. */
  initialLanguage?: Language;
}

export function LanguageProvider({ children, initialLanguage }: LanguageProviderProps) {
  const [language, setLanguageState] = useState<Language>(() => initialLanguage ?? readStoredLanguage());

  const setLanguage = useCallback((next: Language) => {
    setLanguageState(next);
    try {
      localStorage.setItem(LANGUAGE_STORAGE_KEY, next);
    } catch {
      // Per-viewer convenience only — losing persistence across a reload is acceptable, never fatal.
    }
  }, []);

  // Part 二十三: missing key falls back to the other language, then to the raw key itself — never
  // "undefined"/"null"/the literal dot-path shown as if it were content. A console.warn (not
  // thrown) lets development/test catch a missing key without breaking the page for real users.
  const t = useCallback<TranslateFn>(
    (path: string) => {
      const primary = lookup(dictionaries[language], path);
      if (primary !== undefined) return primary;

      const fallbackLanguage: Language = language === 'zh-TW' ? 'en-US' : 'zh-TW';
      const fallback = lookup(dictionaries[fallbackLanguage], path);
      if (fallback !== undefined) {
        if (import.meta.env.DEV) {
          console.warn(`[i18n] Missing translation key "${path}" for language "${language}"`);
        }
        return fallback;
      }

      if (import.meta.env.DEV) {
        console.warn(`[i18n] Missing translation key "${path}" in both languages`);
      }
      return path;
    },
    [language],
  );

  const translateErrorCode = useCallback(
    (code: string) => lookup(dictionaries[language], `errors.${code}`) ?? lookup(dictionaries[language === 'zh-TW' ? 'en-US' : 'zh-TW'], `errors.${code}`),
    [language],
  );

  const value = useMemo<LanguageContextValue>(
    () => ({ language, setLanguage, t, translateErrorCode }),
    [language, setLanguage, t, translateErrorCode],
  );

  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>;
}

export function useTranslation(): LanguageContextValue {
  const ctx = useContext(LanguageContext);
  if (!ctx) {
    throw new Error('useTranslation must be used within a LanguageProvider');
  }
  return ctx;
}
