// Skeleton of the DocAgent API mock for e2e (plan 9.0). Endpoints and fixtures are added per sub-phase
// (SRS 10.5): auth in 9.1, documents in 9.2, SSE ask in 9.3. 9.0 only needs it to be startable.
import { createServer } from "node:http";

const port = Number(process.env.MOCK_API_PORT ?? 4010);

const server = createServer((req, res) => {
  res.setHeader("content-type", "application/json");
  if (req.url === "/health") {
    res.end(JSON.stringify({ status: "ok" }));
    return;
  }
  res.statusCode = 404;
  res.end(JSON.stringify({ type: "about:blank", title: "Not Found", status: 404 }));
});

server.listen(port, "127.0.0.1", () => {
  console.log(`mock API listening on http://127.0.0.1:${port}`);
});
