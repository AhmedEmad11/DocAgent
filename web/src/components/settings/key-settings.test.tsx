import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/test-utils";
import { routerMock } from "@/test/router-mock";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));

import { KeySettings } from "./key-settings";

const KEY = "sk-example-only-not-a-real-key-123456";
const saved = {
  provider: "openai",
  model: "gpt-4.1",
  last4: "3456",
  updatedAt: "2026-11-23T10:00:00Z",
};
type Api = NonNullable<React.ComponentProps<typeof KeySettings>["api"]>;

const makeApi = (over: Partial<Api> = {}): Api => ({
  loadModels: vi.fn(async () => ({
    ok: true as const,
    data: [
      { id: "a/model-1", name: "Model 1" },
      { id: "b/model-2", name: "Model 2" },
    ],
  })),
  saveKey: vi.fn(async (input) => ({
    ok: true as const,
    data: {
      provider: input.provider,
      model: input.model,
      last4: input.apiKey.slice(-4),
      updatedAt: "2026-11-23T11:00:00Z",
    },
  })),
  deleteKey: vi.fn(async () => ({ ok: true as const, data: null })),
  ...over,
});

const keyField = () => screen.getByLabelText("API key");
const modelField = () => screen.getByLabelText("Model");

afterEach(() => vi.restoreAllMocks());

describe("KeySettings (UI-KEY-1 to 6)", () => {
  it("without a key it explains demo mode and the benefits of a key", () => {
    renderWithIntl(<KeySettings saved={null} demo api={makeApi()} />);
    expect(screen.getByTestId("no-key")).toHaveTextContent("free demo model with daily limits");
    expect(screen.getByTestId("no-key")).toHaveTextContent("no daily question limit");
  });

  it("with a saved key it shows provider, model and only the last 4 characters", () => {
    renderWithIntl(<KeySettings saved={saved} demo={false} api={makeApi()} />);
    expect(screen.getByTestId("saved-provider")).toHaveTextContent("openai");
    expect(screen.getByTestId("saved-model")).toHaveTextContent("gpt-4.1");
    expect(screen.getByTestId("saved-last4")).toHaveTextContent("••••3456");
    expect(screen.getByRole("button", { name: "Check and replace key" })).toBeInTheDocument();
  });

  it("the key field is a password input, off autocomplete, never prefilled, with a show toggle", async () => {
    renderWithIntl(<KeySettings saved={saved} demo={false} api={makeApi()} />);
    expect(keyField()).toHaveAttribute("type", "password");
    expect(keyField()).toHaveAttribute("autocomplete", "off");
    expect(keyField()).toHaveValue("");
    await userEvent.type(keyField(), KEY);
    await userEvent.click(screen.getByRole("button", { name: "Show" }));
    expect(keyField()).toHaveAttribute("type", "text");
    await userEvent.click(screen.getByRole("button", { name: "Hide" }));
    expect(keyField()).toHaveAttribute("type", "password");
  });

  it("validates before calling the API and puts focus on the first invalid field", async () => {
    const api = makeApi();
    renderWithIntl(<KeySettings saved={null} demo api={api} />);
    await userEvent.click(screen.getByRole("button", { name: "Check and save key" }));
    expect(screen.getByText("Enter your API key.")).toBeInTheDocument();
    expect(keyField()).toHaveAttribute("aria-invalid", "true");
    expect(keyField()).toHaveFocus();
    await userEvent.type(keyField(), KEY);
    await userEvent.click(screen.getByRole("button", { name: "Check and save key" }));
    expect(screen.getByText("Enter a model name.")).toBeInTheDocument();
    expect(modelField()).toHaveFocus();
    expect(api.saveKey).not.toHaveBeenCalled();
  });

  it("saves, empties the key field at once, shows the saved configuration and re-reads the page data", async () => {
    let release!: () => void;
    const api = makeApi({
      saveKey: vi.fn(
        (input) =>
          new Promise((resolve) => {
            release = () =>
              resolve({
                ok: true as const,
                data: {
                  provider: input.provider,
                  model: input.model,
                  last4: "3456",
                  updatedAt: "x",
                },
              });
          }),
      ) as Api["saveKey"],
    });
    renderWithIntl(<KeySettings saved={null} demo api={api} />);
    await userEvent.selectOptions(screen.getByLabelText("Provider"), "anthropic");
    await userEvent.type(keyField(), KEY);
    await userEvent.type(modelField(), "claude-sonnet");
    await userEvent.click(screen.getByRole("button", { name: "Check and save key" }));

    expect(await screen.findByTestId("saving")).toHaveTextContent(
      "Checking your key with the provider",
    );
    expect(keyField()).toHaveValue(""); // cleared as soon as it is sent (UI-KEY-5)
    expect(api.saveKey).toHaveBeenCalledWith({
      provider: "anthropic",
      model: "claude-sonnet",
      apiKey: KEY,
    });
    release();

    expect(await screen.findByTestId("notice")).toHaveTextContent(
      "Your key was checked and saved.",
    );
    expect(screen.getByTestId("saved-last4")).toHaveTextContent("••••3456");
    expect(screen.getByTestId("saved-provider")).toHaveTextContent("anthropic");
    expect(keyField()).toHaveValue("");
    expect(routerMock.refresh).toHaveBeenCalled();
  });

  it.each([
    ["llm_key_invalid", 400, "The provider did not accept this key", "API key"],
    ["llm_model_unknown", 400, "That model is not available for this key", "Model"],
  ] as const)(
    "a %s refusal shows its message, empties the key and focuses the field it is about",
    async (code, status, text, field) => {
      const api = makeApi({
        saveKey: vi.fn(async () => ({ ok: false as const, problem: { status, title: "x", code } })),
      });
      renderWithIntl(<KeySettings saved={null} demo api={api} />);
      await userEvent.type(keyField(), KEY);
      await userEvent.type(modelField(), "some-model");
      await userEvent.click(screen.getByRole("button", { name: "Check and save key" }));
      expect(await screen.findByRole("alert")).toHaveTextContent(text);
      expect(keyField()).toHaveValue("");
      await waitFor(() => expect(screen.getByLabelText(field)).toHaveFocus());
    },
  );

  it("names the provider when it says the key was not accepted, and the other codes have their own message", async () => {
    const problems = [
      [
        { status: 402, title: "x", code: "llm_no_credit" },
        "Your openrouter account has no credit left.",
      ],
      [{ status: 429, title: "x", retryAfter: 12 }, "Too many requests. Try again in 12 seconds."],
      [
        { status: 503, title: "x", code: "llm_unavailable" },
        "The model provider is not responding.",
      ],
      [
        { status: 502, title: "x", code: "llm_request_rejected" },
        "The provider could not process this request.",
      ],
      [{ status: 409, title: "x" }, "Another change happened at the same time, try again."],
      [{ status: 0, title: "x", code: "network" }, "Cannot reach the service."],
    ] as const;
    const api = makeApi();
    renderWithIntl(<KeySettings saved={null} demo api={api} />);
    for (const [problem, text] of problems) {
      vi.mocked(api.saveKey).mockResolvedValueOnce({ ok: false, problem });
      await userEvent.type(keyField(), KEY);
      await userEvent.type(modelField(), "m");
      await userEvent.click(screen.getByRole("button", { name: "Check and save key" }));
      expect(await screen.findByRole("alert")).toHaveTextContent(text);
      await userEvent.clear(modelField());
    }
  });

  it("loads the tool-capable models with the entered key, offers them for choosing, and still allows typing", async () => {
    const api = makeApi();
    renderWithIntl(<KeySettings saved={null} demo api={api} />);
    const load = screen.getByRole("button", { name: "Load models" });
    expect(load).toBeDisabled();
    await userEvent.type(keyField(), KEY);
    await userEvent.click(load);
    await waitFor(() => expect(api.loadModels).toHaveBeenCalledWith("openrouter", KEY));
    const options = await screen.findAllByRole("option", { hidden: true });
    expect(options.map((o) => o.getAttribute("value")).filter((v) => v?.includes("/"))).toEqual([
      "a/model-1",
      "b/model-2",
    ]);
    expect(screen.getByText(/Choose from 2 models/)).toBeInTheDocument();
    await userEvent.type(modelField(), "my/own-model");
    expect(modelField()).toHaveValue("my/own-model");
  });

  it("falls back to typing when the model list cannot be loaded, and says why", async () => {
    const api = makeApi({
      loadModels: vi.fn(async () => ({
        ok: false as const,
        problem: { status: 400, title: "x", code: "llm_key_invalid" },
      })),
    });
    renderWithIntl(<KeySettings saved={null} demo api={api} />);
    await userEvent.type(keyField(), KEY);
    await userEvent.click(screen.getByRole("button", { name: "Load models" }));
    const failed = await screen.findByTestId("models-failed");
    expect(failed).toHaveTextContent("The provider did not accept this key");
    expect(failed).toHaveTextContent("You can still type the model name yourself.");
    expect(modelField()).toBeEnabled();
  });

  it("changing the provider or the key forgets a loaded list", async () => {
    renderWithIntl(<KeySettings saved={null} demo api={makeApi()} />);
    await userEvent.type(keyField(), KEY);
    await userEvent.click(screen.getByRole("button", { name: "Load models" }));
    await screen.findByText(/Choose from 2 models/);
    await userEvent.selectOptions(screen.getByLabelText("Provider"), "gemini");
    expect(screen.queryByText(/Choose from/)).not.toBeInTheDocument();
  });

  it("deletes after a confirmation that states the consequences", async () => {
    const api = makeApi();
    renderWithIntl(<KeySettings saved={saved} demo api={api} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete my key" }));
    const dialog = screen.getByRole("dialog", { hidden: true });
    expect(dialog).toHaveTextContent("demo model and its daily limits");
    expect(dialog).toHaveTextContent(
      "Documents above the demo limits stay, but you cannot upload new ones until you are under the limit.",
    );
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete key", hidden: true }));
    expect(await screen.findByTestId("notice")).toHaveTextContent("Your key was deleted.");
    expect(screen.getByTestId("no-key")).toBeInTheDocument();
    expect(api.deleteKey).toHaveBeenCalled();
    expect(routerMock.refresh).toHaveBeenCalled();
  });

  it("the key is never logged and never written to browser storage, in any outcome", async () => {
    const spies = (["log", "info", "warn", "error", "debug"] as const).map((m) =>
      vi.spyOn(console, m).mockImplementation(() => {}),
    );
    const api = makeApi();
    vi.mocked(api.saveKey).mockResolvedValueOnce({
      ok: false,
      problem: { status: 400, title: "x", code: "llm_key_invalid" },
    });
    renderWithIntl(<KeySettings saved={null} demo api={api} />);
    for (let i = 0; i < 2; i++) {
      await userEvent.type(keyField(), KEY);
      await userEvent.type(modelField(), "m");
      await userEvent.click(screen.getByRole("button", { name: "Load models" }));
      await userEvent.click(screen.getByRole("button", { name: "Check and save key" }));
      await waitFor(() => expect(api.saveKey).toHaveBeenCalledTimes(i + 1));
      await waitFor(() => expect(keyField()).toHaveValue(""));
      await userEvent.clear(modelField());
    }
    for (const spy of spies) expect(JSON.stringify(spy.mock.calls)).not.toContain("sk-example");
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain("sk-example");
    expect(document.body.innerHTML).not.toContain("sk-example");
    expect(document.cookie).not.toContain("sk-example");
  });

  it("renders in Arabic with Latin key and model fields left to right", () => {
    renderWithIntl(<KeySettings saved={saved} demo={false} api={makeApi()} />, "ar");
    expect(screen.getByLabelText("مفتاح API")).toHaveAttribute("dir", "ltr");
    expect(screen.getByLabelText("النموذج")).toHaveAttribute("dir", "ltr");
    expect(screen.getByTestId("saved-last4")).toHaveAttribute("dir", "ltr");
  });
});
