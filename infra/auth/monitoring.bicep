// Separate guarded Incremental deployment. No app/job deployment or RBAC writes.
targetScope = 'resourceGroup'
param location string
param appName string
@secure()
param recipient string
@minValue(0)
param budgetAmount int = 0
param budgetStartDate string = ''
// Enable only after root verifies existing app/job and actual native dimension values.
param enableAlerts bool = false
param jobFailureState string = 'Failed'
var ownership = { repositoryId: '1426154477', ownerId: '345029828', managedBy: 'repoagent-auth' }
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = { name: '${appName}-logs' }
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights'
  location: location
  kind: 'web'
  tags: union(ownership, { role: 'auth-monitoring' })
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
    DisableIpMasking: false
    IngestionMode: 'LogAnalytics'
  }
}
resource alerts 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${appName}-alerts'
  location: 'global'
  tags: union(ownership, { role: 'auth-monitoring' })
  properties: {
    groupShortName: 'repoauth'
    enabled: true
    emailReceivers: [{ name: 'operator', emailAddress: recipient, useCommonAlertSchema: true }]
  }
}
resource budget 'Microsoft.Consumption/budgets@2024-08-01' = if (budgetAmount > 0) {
  name: '${appName}-monthly-budget'
  properties: {
    amount: budgetAmount
    category: 'Cost'
    timeGrain: 'Monthly'
    // Azure evaluates in the billing currency; omitted endDate uses its ten-year default.
    timePeriod: { startDate: budgetStartDate }
    notifications: toObject([50, 80, 100], threshold => 'actual${threshold}', threshold => {
      enabled: true, operator: 'GreaterThanOrEqualTo', threshold: threshold, thresholdType: 'Actual'
      contactEmails: [recipient], contactGroups: [], contactRoles: []
    })
  }
}
var metricRules = [
  { suffix: 'cleanup-execution-failed', kind: 'jobs', resourceName: '${appName}-cleanup', metric: 'Executions', aggregation: 'Total', operator: 'GreaterThan', threshold: 0, window: 'PT5M', dimensions: [{ name: 'state', operator: 'Include', values: [jobFailureState] }] }
  { suffix: 'http-errors', kind: 'containerApps', resourceName: appName, metric: 'Requests', aggregation: 'Total', operator: 'GreaterThanOrEqual', threshold: 5, window: 'PT5M', dimensions: [{ name: 'statusCodeCategory', operator: 'Include', values: ['5xx'] }] }
  { suffix: 'memory-pressure', kind: 'containerApps', resourceName: appName, metric: 'WorkingSetBytes', aggregation: 'Average', operator: 'GreaterThan', threshold: 429496729, window: 'PT15M', dimensions: [] }
]
resource metrics 'Microsoft.Insights/metricAlerts@2018-03-01' = [for rule in metricRules: if (enableAlerts) {
  name: '${appName}-${rule.suffix}'
  location: 'global'
  tags: union(ownership, { role: 'auth-monitoring' })
  properties: {
    severity: 2
    enabled: true
    autoMitigate: true
    scopes: [resourceId('Microsoft.App/${rule.kind}', rule.resourceName)]
    evaluationFrequency: 'PT5M'
    windowSize: rule.window
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{ name: 'observed', criterionType: 'StaticThresholdCriterion', metricNamespace: 'Microsoft.App/${rule.kind}', metricName: rule.metric, timeAggregation: rule.aggregation, operator: rule.operator, threshold: rule.threshold, dimensions: rule.dimensions, skipMetricValidation: false }]
    }
    actions: [{ actionGroupId: alerts.id }]
  }
}]
var cleanupQuery = format('''
AppMetrics
| where TimeGenerated > ago(15m) and _ResourceId =~ '{0}' and AppRoleName == 'repoagent-auth-cleanup'
| summarize completed=maxif(Max, Name == 'repoagent.auth.cleanup.completed' and TimeGenerated > ago(5m)), failures=maxif(Max, Name == 'repoagent.auth.cleanup.failures'), unknown=maxif(Max, Name == 'repoagent.auth.cleanup.unknown_deadlines'), stale=maxif(Max, Name == 'repoagent.auth.cleanup.stale_cursor_with_overdue_observation'), cycle=maxif(Max, Name == 'repoagent.auth.cleanup.cycle_age_max_seconds'), cycleUnknown=maxif(Max, Name == 'repoagent.auth.cleanup.cycle_age_unknown')
| project Alert=iff(isnull(completed) or isnull(failures) or isnull(unknown) or isnull(stale) or isnull(cycleUnknown) or failures > 0 or unknown > 0 or stale > 0 or cycle >= 21600 or cycleUnknown > 0, 1, 0)
''', insights.id)
var volumeQuery = format('''
union AppMetrics, AppRequests, AppTraces, AppExceptions, AppDependencies
| where TimeGenerated > ago(24h) and _ResourceId =~ '{0}' and _IsBillable == 'true'
| summarize Bytes=sum(_BilledSize)
''', insights.id)
var queryRules = [
  { suffix: 'cleanup-observation', query: cleanupQuery, frequency: 'PT5M', window: 'PT15M', column: 'Alert', threshold: 0 }
  { suffix: 'telemetry-volume', query: volumeQuery, frequency: 'PT15M', window: 'P1D', column: 'Bytes', threshold: 50000000 }
]
resource queries 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for rule in queryRules: if (enableAlerts) {
  name: '${appName}-${rule.suffix}'
  location: location
  tags: union(ownership, { role: 'auth-monitoring' })
  kind: 'LogAlert'
  properties: {
    enabled: true
    severity: 2
    autoMitigate: true
    evaluationFrequency: rule.frequency
    windowSize: rule.window
    scopes: [logs.id]
    criteria: { allOf: [{ query: rule.query, metricMeasureColumn: rule.column, timeAggregation: 'Maximum', operator: 'GreaterThan', threshold: rule.threshold, failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 } }] }
    actions: { actionGroups: [alerts.id] }
  }
}]
