# Deploying Triviador to Azure (free tier)

You run these commands yourself in **PowerShell**, from `C:\Users\anria\source\repos\Triviador`.
Typing them is the point: almost every step here is AI-200 material.

**What you end up with:** the game at `https://triviador.<random>.westeurope.azurecontainerapps.io`, redeployed
automatically every time you push to `main`.

| Piece | Azure / GitHub service | Why it's free |
|---|---|---|
| Server + web page | Azure Container Apps (Consumption) | Monthly free grant; scales to zero when idle |
| Logs | Log Analytics | Capped at 0.1 GB/day, under the 5 GB/month free allowance |
| Container image | GitHub Container Registry | Free (Azure Container Registry has no free tier) |
| Build + deploy | GitHub Actions | Free minutes |
| Azure login from GitHub | Entra ID app + federated credential (OIDC) | Free, and no passwords stored anywhere |
| Cost alert | Cost Management budget | Free |

---

## 0. Tools (once)

```powershell
winget install --id Microsoft.AzureCLI -e
winget install --id Git.Git -e
```

Close and reopen PowerShell afterwards. You also need a GitHub account.

## 1. Sign in and check you're on the free-account subscription

```powershell
az login
az account show --query "{name:name, subscription:id, tenant:tenantId}" -o table
```

The name should be **Azure subscription 1** (or similar) on the free account. If you have more than one,
select the right one: `az account set --subscription "<name or id>"`.

New subscriptions need the Container Apps and Log Analytics providers turned on:

```powershell
az provider register --namespace Microsoft.App --wait
az provider register --namespace Microsoft.OperationalInsights --wait
```

## 2. Cost alert (before creating anything that runs)

```powershell
az deployment sub create --location westeurope --template-file infra/budget.bicep --parameters contactEmail=YOUR-EMAIL
```

You'll get emails at 50% and 100% of $1, and if the month is forecast to go over.
If this fails on the free trial, skip it for now: during the trial the spending limit protects you anyway.
Run it again after you upgrade to Pay-As-You-Go.

## 3. Resource group

Everything lives here, so deleting it removes every cost in one go.

```powershell
az group create --name rg-triviador --location westeurope
```

## 4. Let GitHub Actions deploy without a password (OIDC)

GitHub proves "I am the main branch of your repo" with a short-lived token; Azure trusts that instead of a secret.

```powershell
$sub    = az account show --query id -o tsv
$tenant = az account show --query tenantId -o tsv
$appId  = az ad app create --display-name triviador-github --query appId -o tsv
az ad sp create --id $appId

# Allow it to manage ONLY the triviador resource group.
# If this says the principal doesn't exist, wait 30 seconds and run it again.
az role assignment create --assignee $appId --role Contributor --scope "/subscriptions/$sub/resourceGroups/rg-triviador"
```

Open `infra/github-federated-credential.json` and replace `YOUR-GITHUB-USER` with your GitHub username
(the repo name `Triviador` must match what you create in step 5). Then:

```powershell
az ad app federated-credential create --id $appId --parameters infra/github-federated-credential.json

# Note these three values for step 5:
"AZURE_CLIENT_ID       = $appId"
"AZURE_TENANT_ID       = $tenant"
"AZURE_SUBSCRIPTION_ID = $sub"
```

## 5. Put the code on GitHub

1. On github.com create a new repository named **Triviador** (private is fine). Don't add a README.
2. In the repo: **Settings → Secrets and variables → Actions → Variables tab → New repository variable**,
   and add `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` with the values from step 4.
3. Move the workflow into place (it ships in `infra/` because Claude can't write into `.github` folders on your PC):

```powershell
New-Item -ItemType Directory -Force .github\workflows | Out-Null
Move-Item infra\deploy.yml .github\workflows\deploy.yml
```

4. Push:

```powershell
git init -b main
git add .
git commit -m "Triviador: engine, server, client and Azure deployment"
git remote add origin https://github.com/YOUR-GITHUB-USER/Triviador.git
git push -u origin main
```

Watch it run in the repo's **Actions** tab: tests → build image → deploy.

## 6. First run only: make the image public

GitHub creates the container image as **private**, so Azure can't pull it and the first deploy fails
with an image-pull / revision error. That's expected:

1. On GitHub open your profile → **Packages** → **triviador** → **Package settings**.
2. **Change visibility → Public**. (It only contains the compiled game, no secrets.)
3. Back in **Actions**, open the failed run → **Re-run failed jobs**.

From now on every push just works.

## 7. Play

The run summary shows the address. The first visit after a quiet period takes 10–30 seconds:
the app scaled to zero and is starting up. That's the free tier working as intended.

---

## Checking cost

Portal → **Cost Management → Cost analysis**, scope = your subscription. Expect $0.00.

```powershell
az containerapp show -g rg-triviador -n triviador --query "properties.template.scale" -o json   # minReplicas must be 0
az containerapp replica list -g rg-triviador -n triviador -o table                               # empty when idle
```

## Turning it all off

```powershell
az group delete --name rg-triviador --yes
```

This deletes the app, environment and logs. The budget, Entra app and GitHub repo stay, and they cost nothing.

---

## What each step teaches (AI-200)

- **Container Apps:** environments, revisions (`activeRevisionsMode: Single`), ingress with WebSockets,
  HTTP scale rules, scale to zero, liveness probes, private-registry credentials (see `registryUsername` in `main.bicep`).
- **Infrastructure as code:** Bicep, `az deployment group/sub create`, outputs, secure parameters.
- **Identity:** Entra app registrations, service principals, RBAC scoped to a resource group,
  workload identity federation (OIDC) instead of stored secrets.
- **Monitoring:** Log Analytics workspace behind Container Apps; daily ingestion cap.
  Try: Portal → the container app → **Log stream**, or **Logs** with `ContainerAppConsoleLogs_CL`.
