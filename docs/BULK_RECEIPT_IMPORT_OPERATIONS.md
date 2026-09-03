# Bulk Receipt Import – Operations

This document describes deployment configuration for the bulk receipt import feature. The feature always feeds the existing receipt scan/OCR/review pipeline.

## Backend configuration

```json
{
  "Kestrel": {
    "Limits": {
      "MaxRequestBodySize": 536870912
    }
  },
  "ReceiptImports": {
    "MaxBatchItems": 500,
    "MaxUploadBytes": 536870912,
    "MaxParallelImports": 2,
    "PaperlessPageSize": 100,
    "PaperlessTimeoutSeconds": 60,
    "InboxPath": "/data/receipt-import",
    "FolderEnabled": false,
    "FolderRecursive": true,
    "FolderScanIntervalSeconds": 60,
    "FolderStableAgeSeconds": 10,
    "AutoStart": true,
    "DefaultCurrency": "EUR"
  }
}
```

`MaxUploadBytes` limits one browser bulk-upload request. `PurchaseStorage:MaxReceiptBytes` still limits every individual receipt. The FullWorth.Web BFF must allow at least the same Kestrel request-body size because it streams browser uploads to the backend.

`InboxPath` is deployment-owned. It is never accepted from a browser/API request. The folder feature remains disabled unless `FolderEnabled=true` and the configured directory exists.

## Docker / NAS mount example

Do not hard-code a host-specific mount in the repository compose. Operators may add a read-only bind/CIFS/NFS-backed mount to the backend service:

```yaml
services:
  backend:
    volumes:
      - /mnt/receipts:/data/receipt-import:ro
    environment:
      ReceiptImports__FolderEnabled: "true"
      ReceiptImports__InboxPath: /data/receipt-import
      ReceiptImports__FolderRecursive: "true"
      ReceiptImports__FolderStableAgeSeconds: "10"
```

Read-only is recommended because FullWorth does not need to delete or rename source files. Durable copies are written to the normal `PurchaseStorage` location before OCR begins.

## Paperless-ngx

Create a Paperless API token for the account that may read the intended documents. Configure URL and token from `Käufe -> Belege importieren -> Paperless-ngx`.

Behavior:

- the token is persisted through `FieldCipher`, not as plain text;
- API responses never return the token;
- Paperless access is read-only in this implementation;
- pagination may only continue on the configured scheme/host/port;
- LAN/private Paperless URLs are intentionally supported for self-hosted installations;
- each Paperless document becomes one logical receipt; a multi-page PDF remains one purchase and is expanded by the existing receipt rasterizer.

A useful Paperless setup is to assign a receipt document type/tag and use that as the default query instead of importing every document.

## Folder import behavior

The scanner accepts only the same supported receipt families as the canonical receipt queue: JPG/JPEG, PNG, WebP, HEIC and PDF.

It ignores hidden files, `.tmp`, `.part`, the internal `.fullworth` directory and directory reparse points. Files newer than `FolderStableAgeSeconds` are ignored until a later scan, reducing the chance of importing a file that is still being copied.

The API exposes only relative filenames, counts and byte totals. The configured host/NAS root is excluded from JSON responses.

## Duplicate handling

Duplicate protection is layered:

1. source identity (Paperless document ID, browser SHA-256 identity or folder content fingerprint),
2. SHA-256 exact-content detection in the canonical receipt storage path,
3. existing semantic receipt duplicate review after extraction.

Exact duplicates are skipped. Semantic/uncertain duplicates are not deleted automatically and remain reviewable.

## Operational verification

After deployment:

1. apply the backend migration `20260901211500_BulkReceiptImports` through normal application startup;
2. open `Käufe -> Belege importieren`;
3. upload two different image receipts with `Direkt analysieren` disabled and verify two independent draft jobs are created;
4. upload one of those files again in a new batch and verify it is reported as a duplicate;
5. if Paperless is used, test the connection before preview/import;
6. if an import folder is used, verify the container can read the mount and that the preview lists only relative paths;
7. start pending receipts and verify they continue through the existing receipt review flow.

## Rollback

The migration down path removes only bulk-import operational metadata and the Paperless connection row. It does not delete Purchases, PurchaseDocuments or ReceiptScanJobs already created by imports.
