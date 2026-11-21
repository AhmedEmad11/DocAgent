// Parameters for infra/main.bicep. Contains NO secrets: they are read from environment variables at deploy time
// (the deploy workflow exports them from GitHub secrets; locally `export` them first, see docs/DEPLOYMENT.md).
using './main.bicep'

param appName = 'docagent-api'
param image = readEnvironmentVariable('DOCAGENT_IMAGE', 'ghcr.io/OWNER/docagent-api:latest') // the workflow passes ghcr.io/<owner>/docagent-api:<git sha>
param minReplicas = 0
param cpu = '0.5'
param memory = '1Gi'

param logsMode = 'logAnalytics'
param logRetentionDays = 30
param logDailyQuotaGb = 1

param emailFromAddress = readEnvironmentVariable('EMAIL_FROM_ADDRESS')
param emailFromName = 'DocAgent'
param corsOrigins = []          // Phase 9: ['https://<ui host>']
param allowedReturnUrls = []    // Phase 9: ['https://<ui host>']

param googleClientId = readEnvironmentVariable('OAUTH_GOOGLE_CLIENT_ID', '')
param gitHubClientId = readEnvironmentVariable('OAUTH_GITHUB_CLIENT_ID', '')

// ---- secrets (environment variables)
param neonConnectionString = readEnvironmentVariable('NEON_CONNECTION_STRING')
param openRouterApiKey = readEnvironmentVariable('OPENROUTER_API_KEY')
param dataProtectionCertificatePfxBase64 = readEnvironmentVariable('DP_CERT_PFX_BASE64')
param dataProtectionCertificatePassword = readEnvironmentVariable('DP_CERT_PASSWORD')
param smtpUsername = readEnvironmentVariable('SMTP_USERNAME')
param smtpPassword = readEnvironmentVariable('SMTP_PASSWORD')
param googleClientSecret = readEnvironmentVariable('OAUTH_GOOGLE_CLIENT_SECRET', '')
param gitHubClientSecret = readEnvironmentVariable('OAUTH_GITHUB_CLIENT_SECRET', '')
