"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ProblemAlert } from "@/components/ui/problem-alert";
import { Label } from "@/components/ui/label";
import { Input } from "@/components/ui/input";
import { dynamicT } from "@/i18n/dynamic";
import { Link } from "@/i18n/navigation";
import type { SavedKey } from "@/lib/api/llm-key";
import type { ApiProblem } from "@/lib/api/problem";
import { deleteKey, loadModels, saveKey, type ModelInfo } from "@/lib/keys/client";
import {
  PROVIDERS,
  canListModels,
  validateKeyForm,
  type KeyField,
  type KeyFieldError,
} from "@/lib/keys/schemas";

type Api = { loadModels: typeof loadModels; saveKey: typeof saveKey; deleteKey: typeof deleteKey };

/**
 * LLM key settings (UI-KEY-1 to 6). The key field is a password input without autocomplete that is never prefilled and is emptied after
 * every submit; the key is held only in this component's state, sent in a same-origin request to our BFF and never written to a URL,
 * storage or a log. Only the last 4 characters of a saved key are ever shown (they come from the API).
 */
export function KeySettings({
  saved: initial,
  demo,
  api = { loadModels, saveKey, deleteKey },
}: {
  saved: SavedKey | null;
  demo: boolean;
  /** Test seam. */
  api?: Api;
}) {
  const t = useTranslations("Settings");
  const tk = dynamicT(t);
  const router = useRouter();
  const ids = useId();
  const [saved, setSaved] = useState(initial);
  const [provider, setProvider] = useState<string>(initial?.provider ?? "openrouter");
  const [key, setKey] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [model, setModel] = useState("");
  const [models, setModels] = useState<ModelInfo[] | null>(null);
  const [listing, setListing] = useState(false);
  const [listProblem, setListProblem] = useState<ApiProblem | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<KeyField, KeyFieldError>>>({});
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const [notice, setNotice] = useState<"saved" | "deleted" | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteProblem, setDeleteProblem] = useState<ApiProblem | null>(null);
  const keyRef = useRef<HTMLInputElement>(null);
  const modelRef = useRef<HTMLInputElement>(null);
  const focusAfter = useRef<KeyField | null>(null);
  const summaryRef = useRef<HTMLDivElement>(null);
  const [summarySignal, setSummarySignal] = useState(0);

  useEffect(() => {
    // A failed validation puts focus on the error summary, whose links lead to the fields (UI-A11Y-3).
    if (summarySignal > 0) summaryRef.current?.focus();
  }, [summarySignal]);

  useEffect(() => {
    // Focus goes to the field the message is about (SRS 10.6), after the render that shows it.
    const target = focusAfter.current;
    if (!target) return;
    focusAfter.current = null;
    (target === "key" ? keyRef : modelRef).current?.focus();
  });

  const resetModels = () => {
    setModels(null);
    setListProblem(null);
  };

  const listModels = async () => {
    setListing(true);
    setListProblem(null);
    const res = await api.loadModels(provider, key);
    setListing(false);
    if (res.ok) setModels(res.data);
    else {
      setModels(null);
      setListProblem(res.problem);
    }
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (saving) return;
    setNotice(null);
    setProblem(null);
    const errors = validateKeyForm({ provider, key, model });
    setFieldErrors(errors);
    if (errors.key || errors.model) {
      setSummarySignal((n) => n + 1);
      return;
    }
    setSaving(true);
    const submitted = { provider, model, apiKey: key };
    setKey(""); // UI-KEY-5: gone from the form as soon as it is sent
    setShowKey(false);
    const res = await api.saveKey(submitted);
    setSaving(false);
    if (res.ok) {
      setSaved(res.data);
      setModel("");
      resetModels();
      setNotice("saved");
      router.refresh(); // limits and the demo banner follow the new mode
      return;
    }
    setProblem(res.problem);
    if (res.problem.code === "llm_key_invalid") focusAfter.current = "key";
    else if (res.problem.code === "llm_model_unknown") focusAfter.current = "model";
  };

  const remove = async () => {
    setDeleting(true);
    setDeleteProblem(null);
    const res = await api.deleteKey();
    setDeleting(false);
    if (!res.ok) return setDeleteProblem(res.problem);
    setSaved(null);
    setConfirmDelete(false);
    setNotice("deleted");
    router.refresh();
  };

  const keyErrorId = `${ids}-key-error`;
  const modelErrorId = `${ids}-model-error`;

  return (
    <div className="space-y-8">
      <section aria-labelledby="saved-title" className="space-y-3" data-testid="saved-section">
        <h2 id="saved-title" className="text-lg font-semibold">
          {t("saved.title")}
        </h2>
        {saved ? (
          <div className="space-y-3">
            <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-[auto_1fr]">
              <dt className="text-muted-foreground">{t("saved.provider")}</dt>
              <dd data-testid="saved-provider">{saved.provider}</dd>
              <dt className="text-muted-foreground">{t("saved.model")}</dt>
              <dd dir="ltr" className="text-start" data-testid="saved-model">
                {saved.model}
              </dd>
              <dt className="text-muted-foreground">{t("saved.key")}</dt>
              <dd dir="ltr" className="text-start" data-testid="saved-last4">
                ••••{saved.last4}
              </dd>
            </dl>
            <p className="text-sm text-muted-foreground">{t("saved.limits")}</p>
            <Button type="button" variant="outline" onClick={() => setConfirmDelete(true)}>
              {t("delete.action")}
            </Button>
          </div>
        ) : (
          <div className="space-y-2 text-sm" data-testid="no-key">
            <p>{demo ? t("none.demo") : t("none.noDemo")}</p>
            <p className="text-muted-foreground">{t("none.benefits")}</p>
          </div>
        )}
        {notice ? (
          <p
            role="status"
            className="rounded-md border border-border bg-muted p-3 text-sm"
            data-testid="notice"
          >
            {t(`notice.${notice}`)}
          </p>
        ) : null}
      </section>

      <section aria-labelledby="form-title" className="space-y-4">
        <h2 id="form-title" className="text-lg font-semibold">
          {saved ? t("form.replaceTitle") : t("form.saveTitle")}
        </h2>
        <form
          onSubmit={(e) => void submit(e)}
          noValidate
          className="space-y-5"
          data-testid="key-form"
        >
          {fieldErrors.key || fieldErrors.model ? (
            <div
              ref={summaryRef}
              tabIndex={-1}
              role="alert"
              data-testid="form-summary"
              className="rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <p className="font-medium">{t("form.summaryTitle")}</p>
              <ul className="mt-1 list-disc space-y-0.5 ps-5">
                {fieldErrors.key ? (
                  <li>
                    <a href={`#${ids}-key`} className="text-primary underline underline-offset-4">
                      {t("form.key")}: {tk(`errors.${fieldErrors.key}`)}
                    </a>
                  </li>
                ) : null}
                {fieldErrors.model ? (
                  <li>
                    <a href={`#${ids}-model`} className="text-primary underline underline-offset-4">
                      {t("form.model")}: {tk(`errors.${fieldErrors.model}`)}
                    </a>
                  </li>
                ) : null}
              </ul>
            </div>
          ) : null}
          {problem ? <ProblemAlert problem={problem} ctx={{ area: "key-save", provider }} /> : null}

          <div className="space-y-1.5">
            <Label htmlFor={`${ids}-provider`}>{t("form.provider")}</Label>
            <select
              id={`${ids}-provider`}
              value={provider}
              disabled={saving}
              onChange={(e) => {
                setProvider(e.target.value);
                resetModels();
                setModel("");
              }}
              className="h-10 w-full rounded-md border border-input bg-background px-3 text-base focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
            >
              {PROVIDERS.map((p) => (
                <option key={p} value={p}>
                  {tk(`providers.${p}`)}
                </option>
              ))}
            </select>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor={`${ids}-key`}>{t("form.key")}</Label>
            <div className="flex gap-2">
              <Input
                ref={keyRef}
                id={`${ids}-key`}
                name="apiKey"
                type={showKey ? "text" : "password"}
                autoComplete="off"
                autoCapitalize="off"
                autoCorrect="off"
                spellCheck={false}
                dir="ltr"
                value={key}
                disabled={saving}
                onChange={(e) => {
                  setKey(e.target.value);
                  if (models) resetModels();
                }}
                aria-invalid={fieldErrors.key ? true : undefined}
                aria-describedby={`${ids}-key-hint${fieldErrors.key ? ` ${keyErrorId}` : ""}`}
                data-testid="key-input"
              />
              <Button
                type="button"
                variant="outline"
                onClick={() => setShowKey((v) => !v)}
                aria-pressed={showKey}
              >
                {showKey ? t("form.hide") : t("form.show")}
              </Button>
            </div>
            <p id={`${ids}-key-hint`} className="text-sm text-muted-foreground">
              {t("form.keyHint")}
            </p>
            {fieldErrors.key ? (
              <p id={keyErrorId} className="text-sm text-destructive">
                {tk(`errors.${fieldErrors.key}`)}
              </p>
            ) : null}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor={`${ids}-model`}>{t("form.model")}</Label>
            <Input
              ref={modelRef}
              id={`${ids}-model`}
              name="model"
              dir="ltr"
              autoComplete="off"
              autoCapitalize="off"
              spellCheck={false}
              list={models ? `${ids}-models` : undefined}
              value={model}
              disabled={saving}
              onChange={(e) => setModel(e.target.value)}
              aria-invalid={fieldErrors.model ? true : undefined}
              aria-describedby={`${ids}-model-hint${fieldErrors.model ? ` ${modelErrorId}` : ""}`}
              data-testid="model-input"
            />
            {models ? (
              <datalist id={`${ids}-models`}>
                {models.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}
                  </option>
                ))}
              </datalist>
            ) : null}
            <p id={`${ids}-model-hint`} className="text-sm text-muted-foreground">
              {models ? t("form.modelHintList", { count: models.length }) : t("form.modelHint")}
            </p>
            {fieldErrors.model ? (
              <p id={modelErrorId} className="text-sm text-destructive">
                {tk(`errors.${fieldErrors.model}`)}
              </p>
            ) : null}
            <div className="flex flex-wrap items-center gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={!canListModels(key) || listing || saving}
                aria-busy={listing}
                onClick={() => void listModels()}
              >
                {listing ? t("form.loadingModels") : t("form.loadModels")}
              </Button>
              {!canListModels(key) ? (
                <span className="text-sm text-muted-foreground">
                  {t("form.loadModelsNeedsKey")}
                </span>
              ) : null}
            </div>
            {listProblem ? (
              <div className="space-y-2" data-testid="models-failed">
                <ProblemAlert problem={listProblem} ctx={{ area: "key-save", provider }} />
                <p className="text-sm text-muted-foreground">{t("form.typeInstead")}</p>
              </div>
            ) : null}
          </div>

          <p className="text-sm text-muted-foreground">
            {t("form.storage")}{" "}
            <Link href="/privacy" className="text-primary underline underline-offset-4">
              {t("form.privacyLink")}
            </Link>
          </p>

          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={saving} aria-busy={saving}>
              {saved ? t("form.replace") : t("form.save")}
            </Button>
            {saving ? (
              <span role="status" className="text-sm" data-testid="saving">
                {t("form.checking")}
              </span>
            ) : null}
          </div>
        </form>
      </section>

      <ConfirmDialog
        open={confirmDelete}
        title={t("delete.title")}
        confirmLabel={deleting ? t("delete.deleting") : t("delete.confirm")}
        cancelLabel={t("delete.cancel")}
        pending={deleting}
        onConfirm={() => void remove()}
        onCancel={() => {
          setConfirmDelete(false);
          setDeleteProblem(null);
        }}
        error={
          deleteProblem ? <ProblemAlert problem={deleteProblem} ctx={{ area: "key" }} /> : null
        }
      >
        <p>{t("delete.body")}</p>
        <p>{t("delete.documents")}</p>
      </ConfirmDialog>
    </div>
  );
}
