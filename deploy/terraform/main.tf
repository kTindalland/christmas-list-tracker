terraform {
  required_version = ">= 1.0"
  required_providers {
    render = {
      source  = "render-oss/render"
      version = "~> 1.9.0"
    }
    supabase = {
      source  = "supabase/supabase"
      version = "~> 1.0"
    }
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.0"
    }
  }
}

# --- NATIVE .ENV PARSER ---
locals {
  env_raw = {
    for line in split("\n", file("${path.module}/.env")) :
    trimspace(split("=", line)[0]) => trimspace(split("=", line)[1])
    if length(regexall("^\\s*[^#=]+\\s*=", line)) > 0
  }

  env = {
    for k, v in local.env_raw : k => sensitive(v)
  }
}

# --- PROVIDERS CONFIGURATION ---
provider "render" {
  api_key  = local.env["RENDER_API_KEY"]
  owner_id = local.env["RENDER_OWNER_ID"]
}

provider "supabase" {
  access_token = local.env["SUPABASE_ACCESS_TOKEN"]
}

provider "cloudflare" {
  api_token = local.env["CLOUDFLARE_API_TOKEN"]
}

# --- 1. SUPABASE DATABASE ---

resource "supabase_project" "christmas_db" {
  organization_id   = local.env["SUPABASE_ORG_ID"]
  name              = "family-christmas-lists-db"
  database_password = local.env["DATABASE_PASSWORD"]
  region            = "eu-west-1"
}

# --- 2. CLOUDFLARE R2 BUCKET (FOR PHOTO UPLOADS) ---

resource "cloudflare_r2_bucket" "christmas_photos" {
  account_id = local.env["CLOUDFLARE_ACCOUNT_ID"]
  name       = "family-christmas-list-photos"
  location   = "weur"
}

# --- 3. RENDER WEB SERVICE (BLAZOR APP) ---

resource "render_web_service" "blazor_app" {
  name   = "family-christmas-list"
  plan   = "free"
  region = "frankfurt"

  runtime_source = {
    docker = {
      repo_url    = "https://github.com/kTindalland/christmas-list-tracker"
      branch      = "main"
      auto_deploy = true
    }
  }

  env_vars = {
    "ConnectionStrings__DefaultConnection" = {
      value = "Host=db.${supabase_project.christmas_db.id}.supabase.co;Database=postgres;Username=postgres;Password=${local.env["DATABASE_PASSWORD"]}"
    }
    "R2__ServiceUrl" = {
      value = local.env["R2_SERVICE_URL"]
    }
    "R2__BucketName" = {
      value = cloudflare_r2_bucket.christmas_photos.name
    }
    "R2__AccessKeyId" = {
      value = local.env["R2_ACCESS_KEY_ID"]
    }
    "R2__SecretAccessKey" = {
      value = local.env["R2_SECRET_ACCESS_KEY"]
    }
  }
}

# --- OUTPUTS ---

output "supabase_api_url" {
  value     = "https://${supabase_project.christmas_db.id}.supabase.co"
  sensitive = false
}

output "render_service_url" {
  value = "https://${render_web_service.blazor_app.slug}.onrender.com"
}
