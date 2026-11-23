// Server-Sent Events parser (WHATWG HTML, "Server-sent events"): feed it text as it arrives, in any chunking; it emits one
// message per blank-line-terminated block. Pure, so it is unit tested with split chunks.
export interface SseMessage {
  event: string;
  data: string;
}

export interface SseParser {
  /** Feed the next piece of text. */
  push(chunk: string): void;
  /** End of stream: a final block without its blank line is still delivered (the API always ends blocks, but be forgiving). */
  end(): void;
}

export function createSseParser(onMessage: (message: SseMessage) => void): SseParser {
  let buffer = "";
  let event = "";
  let data: string[] = [];
  let hasData = false;
  let sawCr = false;

  const dispatch = () => {
    if (hasData) onMessage({ event: event || "message", data: data.join("\n") });
    event = "";
    data = [];
    hasData = false;
  };

  const line = (text: string) => {
    if (text === "") return dispatch();
    if (text.startsWith(":")) return; // comment / keep-alive
    const colon = text.indexOf(":");
    const field = colon === -1 ? text : text.slice(0, colon);
    let value = colon === -1 ? "" : text.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "event") event = value;
    else if (field === "data") {
      data.push(value);
      hasData = true;
    }
    // `id` and `retry` are not used by this API.
  };

  return {
    push(chunk) {
      let text = buffer + chunk;
      // A CRLF split across two chunks: drop the LF that follows a CR we already treated as a line end.
      if (sawCr && text.startsWith("\n")) text = text.slice(1);
      sawCr = false;
      let start = 0;
      for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (ch !== "\n" && ch !== "\r") continue;
        line(text.slice(start, i));
        if (ch === "\r") {
          if (text[i + 1] === "\n") i++;
          else if (i + 1 === text.length) sawCr = true;
        }
        start = i + 1;
      }
      buffer = text.slice(start);
    },
    end() {
      if (buffer) line(buffer);
      buffer = "";
      dispatch();
    },
  };
}
