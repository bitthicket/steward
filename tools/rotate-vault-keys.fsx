#!/usr/bin/env dotnet fsi
// rotate-vault-keys.fsx
// Manual admin script to re-encrypt all credential_vault rows under a new key.
//
// Prerequisites:
//   1. Set the NEW key in STEWARD_VAULT_KEY (and optionally STEWARD_VAULT_KEY_VERSION).
//   2. Set the OLD key in STEWARD_VAULT_KEY_PREVIOUS (and optionally STEWARD_VAULT_KEY_PREVIOUS_VERSION).
//   3. Set the database connection string in STEWARD_CONNECTION_STRING.
//
// Usage:
//   dotnet fsi tools/rotate-vault-keys.fsx
//
// Safety:
//   - This script operates directly on the database; run against a backup first.
//   - Progress is printed to stdout; no plaintext is logged.

#r "nuget: Npgsql, 10.0.1"

open System
open System.Security.Cryptography
open Npgsql

let envOrFail name =
    match Environment.GetEnvironmentVariable(name) with
    | null | "" -> failwithf "%s is required" name
    | v -> v

let decodeKey name =
    let b64 = envOrFail name
    let bytes = Convert.FromBase64String(b64)
    if bytes.Length <> 32 then failwithf "%s must be 32 bytes (base64-encoded)" name
    bytes

let connString = envOrFail "STEWARD_CONNECTION_STRING"
let newKey = decodeKey "STEWARD_VAULT_KEY"
let newVersion =
    match Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY_VERSION") with
    | null | "" -> 1
    | v -> int v

let oldKey = decodeKey "STEWARD_VAULT_KEY_PREVIOUS"
let oldVersion =
    match Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS_VERSION") with
    | null | "" -> 0
    | v -> int v

let nonceSize = 12
let tagSize = 16

let decrypt (key: byte[]) (nonce: byte[]) (ciphertextWithTag: byte[]) : byte[] =
    if ciphertextWithTag.Length < tagSize then
        failwith "Ciphertext too short"
    let ctLen = ciphertextWithTag.Length - tagSize
    let ciphertext = Array.zeroCreate<byte> ctLen
    let tag = Array.zeroCreate<byte> tagSize
    Buffer.BlockCopy(ciphertextWithTag, 0, ciphertext, 0, ctLen)
    Buffer.BlockCopy(ciphertextWithTag, ctLen, tag, 0, tagSize)
    use aes = new AesGcm(key, tagSize)
    let plaintext = Array.zeroCreate<byte> ctLen
    aes.Decrypt(nonce, ciphertext, tag, plaintext)
    plaintext

let encrypt (key: byte[]) (plaintext: byte[]) : byte[] * byte[] =
    use aes = new AesGcm(key, tagSize)
    let nonce = RandomNumberGenerator.GetBytes(nonceSize)
    let ciphertext = Array.zeroCreate<byte> plaintext.Length
    let tag = Array.zeroCreate<byte> tagSize
    aes.Encrypt(nonce, plaintext, ciphertext, tag)
    let combined = Array.zeroCreate<byte> (ciphertext.Length + tagSize)
    Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length)
    Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tagSize)
    (nonce, combined)

use dataSource = NpgsqlDataSource.Create(connString)
use conn = dataSource.OpenConnection()

// Fetch all rows that are not already on the new key version
use selectCmd = conn.CreateCommand()
selectCmd.CommandText <- "SELECT id, tenant_id, ref, ciphertext, nonce, key_version FROM credential_vault WHERE key_version <> $1"
selectCmd.Parameters.AddWithValue("$1", newVersion) |> ignore

use reader = selectCmd.ExecuteReader()
let rows = ResizeArray<(Guid * Guid * string * byte[] * byte[] * int)>()
while reader.Read() do
    rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4), reader.GetInt32(5)))
reader.Close()

printfn "Found %d row(s) to rotate." rows.Count

let mutable rotated = 0
let mutable failed = 0

for (id, tenantId, ref, ciphertext, nonce, keyVersion) in rows do
    if keyVersion = newVersion then
        printfn "  %s (tenant %O) already on current version — skipping." ref tenantId
    else
        try
            let plaintext =
                if keyVersion = oldVersion then
                    decrypt oldKey nonce ciphertext
                else
                    failwithf "Unknown key version %d for ref %s" keyVersion ref

            let newNonce, newCiphertext = encrypt newKey plaintext

            use updateCmd = conn.CreateCommand()
            updateCmd.CommandText <-
                "UPDATE credential_vault SET ciphertext = $1, nonce = $2, key_version = $3, updated_at = now() WHERE id = $4"
            updateCmd.Parameters.AddWithValue("$1", newCiphertext) |> ignore
            updateCmd.Parameters.AddWithValue("$2", newNonce) |> ignore
            updateCmd.Parameters.AddWithValue("$3", newVersion) |> ignore
            updateCmd.Parameters.AddWithValue("$4", id) |> ignore
            let affected = updateCmd.ExecuteNonQuery()
            if affected = 1 then
                printfn "  Rotated %s (tenant %O) version %d -> %d." ref tenantId keyVersion newVersion
                rotated <- rotated + 1
            else
                printfn "  WARNING: %s update affected %d rows." ref affected
                failed <- failed + 1
        with ex ->
            printfn "  FAILED: %s (tenant %O) — %s" ref tenantId ex.Message
            failed <- failed + 1

printfn ""
printfn "Rotation complete: %d rotated, %d failed." rotated failed
if failed > 0 then Environment.Exit(1)
