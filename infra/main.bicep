// DocAgent on Azure Container Apps (Phase 8). Resource-group scoped:
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
// Creates: Log Analytics workspace (optional), Container Apps environment (Consumption plan), one Container App.
// The database (Neon), SMTP (Brevo) and the LLM key (OpenRouter) are external and arrive as parameters. See docs/DEPLOYMENT.md.
targetScope = 'resourceGroup'

// ---------------------------------------------------------------- general
@description('Name of the Container App (also the first part of its public host name: <name>.<env-id>.<region>.azurecontainerapps.io). Lowercase letters, digits and hyphens, at most 32 characters.')
@minLength(2)
@maxLength(32)
param appName string = 'docagent-api'

@description('Azure region. Defaults to the resource group\'s region.')
param location string = resourceGroup().location

@description('Full image reference, e.g. ghcr.io/<owner>/docagent-api:<git-sha>. Use the commit SHA tag, never :latest, so every deploy is a new revision and rollback is possible.')
param image string

// ---------------------------------------------------------------- sizing
@description('vCPU per replica. Valid Consumption combinations: 0.25/0.5Gi, 0.5/1Gi, 0.75/1.5Gi, 1.0/2Gi ...')
param cpu string = '0.5'

@description('Memory per replica (see cpu). Measured: see docs/DEPLOYMENT.md "Sizing".')
param memory string = '1Gi'

@description('0 = scale to zero when idle (free-grant friendly, cold start of several seconds). Set 1 to keep one replica warm (uses the free grant faster).')
@allowed([ 0, 1 ])
param minReplicas int = 0

// ---------------------------------------------------------------- logs
@description('logAnalytics = send console logs to a Log Analytics workspace (pay-per-GB after the monthly free allowance; see cost notes). none = no workspace; logs are then only available live via "az containerapp logs show".')
@allowed([ 'logAnalytics', 'none' ])
param logsMode string = 'logAnalytics'

@description('Log Analytics retention in days (30 is the minimum for the PerGB2018 SKU).')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

@description('Daily ingestion cap in GB, so a log flood cannot create a bill. -1 = no cap.')
param logDailyQuotaGb int = 1

// ---------------------------------------------------------------- non-secret app settings
@description('Exact origins of the web UI allowed by CORS (Phase 9). Empty = CORS off.')
param corsOrigins array = []

@description('Where the API may redirect after an external (Google/GitHub) sign-in: the UI origin(s). Empty until the UI exists.')
param allowedReturnUrls array = []

@description('Sender address for confirmation and reset emails. With Brevo it must be a verified sender (no domain yet: a single verified address).')
param emailFromAddress string

@description('Sender display name.')
param emailFromName string = 'DocAgent'

@description('Google OAuth client id (not secret). Empty = Google sign-in off.')
param googleClientId string = ''

@description('GitHub OAuth client id (not secret). Empty = GitHub sign-in off.')
param gitHubClientId string = ''

// ---------------------------------------------------------------- secrets (become Container Apps secrets; never in the repo)
@secure()
@description('Npgsql connection string of the Neon database (direct, not pooled, endpoint), key-value form.')
param neonConnectionString string

@secure()
@description('Operator OpenRouter key for demo mode.')
param openRouterApiKey string

@secure()
@description('Base64 of the Data Protection certificate (.pfx). Encrypts the key ring stored in the database.')
param dataProtectionCertificatePfxBase64 string

@secure()
@description('Password of the .pfx.')
param dataProtectionCertificatePassword string

@secure()
@description('Brevo SMTP login (the account\'s SMTP login, not the website password).')
param smtpUsername string

@secure()
@description('Brevo SMTP key.')
param smtpPassword string

@secure()
@description('Google OAuth client secret. Empty = Google sign-in off.')
param googleClientSecret string = ''

@secure()
@description('GitHub OAuth client secret. Empty = GitHub sign-in off.')
param gitHubClientSecret string = ''

// ---------------------------------------------------------------- optional private registry (GHCR public package needs none)
@description('Registry host, e.g. ghcr.io. Only needed for a private image.')
param registryServer string = ''
param registryUsername string = ''
@secure()
param registryPassword string = ''

// ================================================================ resources
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (logsMode == 'logAnalytics') {
  name: '${appName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: logRetentionDays
    workspaceCapping: { dailyQuotaGb: logDailyQuotaGb }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${appName}-env'
  location: location
  properties: {
    appLogsConfiguration: logsMode == 'logAnalytics' ? {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    } : null
  }
}

// The public host name is <app>.<environment default domain>: known here without a second deployment, so AllowedHosts (S-18) and the
// health probes' Host header can use it.
var fqdn = '${appName}.${environment.properties.defaultDomain}'

var useRegistry = !empty(registryServer) && !empty(registryPassword)
var useGoogle = !empty(googleClientId) && !empty(googleClientSecret)
var useGitHub = !empty(gitHubClientId) && !empty(gitHubClientSecret)

var secrets = concat(
  [
    { name: 'neon-connection', value: neonConnectionString }
    { name: 'openrouter-key', value: openRouterApiKey }
    { name: 'dp-cert-pfx', value: dataProtectionCertificatePfxBase64 }
    { name: 'dp-cert-password', value: dataProtectionCertificatePassword }
    { name: 'smtp-username', value: smtpUsername }
    { name: 'smtp-password', value: smtpPassword }
  ],
  useGoogle ? [ { name: 'google-client-secret', value: googleClientSecret } ] : [],
  useGitHub ? [ { name: 'github-client-secret', value: gitHubClientSecret } ] : [],
  useRegistry ? [ { name: 'registry-password', value: registryPassword } ] : []
)

var corsEnv = [for (origin, i) in corsOrigins: { name: 'Cors__AllowedOrigins__${i}', value: origin }]
var returnUrlEnv = [for (url, i) in allowedReturnUrls: { name: 'Authentication__External__AllowedReturnUrls__${i}', value: url }]

var env = flatten([
  [
    { name: 'AllowedHosts', value: fqdn }
    { name: 'ConnectionStrings__docagentdb', secretRef: 'neon-connection' }
    { name: 'Llm__Demo__OpenRouterApiKey', secretRef: 'openrouter-key' }
    { name: 'DataProtection__CertificatePfxBase64', secretRef: 'dp-cert-pfx' }
    { name: 'DataProtection__CertificatePassword', secretRef: 'dp-cert-password' }
    { name: 'Email__Smtp__Username', secretRef: 'smtp-username' }
    { name: 'Email__Smtp__Password', secretRef: 'smtp-password' }
    { name: 'Email__FromAddress', value: emailFromAddress }
    { name: 'Email__FromName', value: emailFromName }
  ]
  corsEnv
  returnUrlEnv
  useGoogle ? [
    { name: 'Authentication__Google__ClientId', value: googleClientId }
    { name: 'Authentication__Google__ClientSecret', secretRef: 'google-client-secret' }
  ] : []
  useGitHub ? [
    { name: 'Authentication__GitHub__ClientId', value: gitHubClientId }
    { name: 'Authentication__GitHub__ClientSecret', secretRef: 'github-client-secret' }
  ] : []
])

// The probes come from inside the environment with the pod's IP as Host; AllowedHosts would answer 400, so they send the public host name.
var probeHeaders = [ { name: 'Host', value: fqdn } ]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      // Single revision: a new deploy replaces the old one; rollback = activate the previous revision (docs/DEPLOYMENT.md).
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false // http is redirected to https at the ingress
      }
      secrets: secrets
      registries: useRegistry ? [
        { server: registryServer, username: registryUsername, passwordSecretRef: 'registry-password' }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: { cpu: json(cpu), memory: memory }
          env: env
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health/live', port: 8080, httpHeaders: probeHeaders }
              initialDelaySeconds: 2
              periodSeconds: 3
              failureThreshold: 20
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080, httpHeaders: probeHeaders }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              // Includes the database (Neon may need a few seconds to wake): traffic is held back until it answers.
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080, httpHeaders: probeHeaders }
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        // Exactly one replica: the ingestion worker, the per-process rate limits/throttles (auth, ask/upload concurrency, the shared
        // demo-key throttle) and job recovery at startup all assume a single instance (SECURITY_REVIEW residual risk 2). Do not raise it.
        maxReplicas: 1
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '20' } } }
        ]
      }
    }
  }
}

output fqdn string = fqdn
output url string = 'https://${fqdn}'
output appName string = app.name
output environmentDefaultDomain string = environment.properties.defaultDomain
