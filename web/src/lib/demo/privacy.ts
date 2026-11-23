// The API's demo privacy notice is English only (PRIV-1). The UI shows a translation when the API's text is exactly the one this UI
// knows (OI-13); anything else is shown as given. Two tests keep this in sync: `messages/en.json` `Demo.privacyNotice` must equal this
// constant, and this constant must equal the API's `DemoNoKeyRouteProvider.PrivacyNotice` (read from the API source), so a change on the
// API side fails the web tests until the translations are updated.
export const KNOWN_PRIVACY_NOTICE_EN =
  "Demo mode: your question and the document excerpts used to answer it are sent to a free third-party language model through OpenRouter. The model's provider may log them and use them. Do not use demo mode for confidential documents; add your own LLM API key (PUT /api/v1/me/llm-key) to choose your provider.";

export const isKnownPrivacyNotice = (text: string): boolean =>
  text.trim() === KNOWN_PRIVACY_NOTICE_EN;
