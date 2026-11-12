namespace DocAgent.Core.Entities;

public enum DocumentStatus { Pending, Processing, Ready, Failed }

public enum IngestionJobState { Queued, Running, Succeeded, Failed }

public enum LlmProvider { OpenRouter, Anthropic, OpenAI, Gemini }
