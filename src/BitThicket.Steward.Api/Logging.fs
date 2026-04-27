namespace BitThicket.Steward.Api

open System
open Serilog.Core
open Serilog.Events

/// Redacts sensitive values from Serilog destructured objects.
/// Matches property names (case-insensitive) that contain accessToken, refreshToken,
/// password, secret, apiSecret, apiKey, or privateKey.
type SecretMaskingPolicy() =
    static let sensitivePatterns =
        [ "accessToken"; "refreshToken"; "password"; "secret"; "apiSecret"; "apiKey"; "privateKey" ]
        |> List.map (fun s -> s.ToLowerInvariant())

    static let isSensitive (name: string) : bool =
        let lower = name.ToLowerInvariant()
        sensitivePatterns |> List.exists (fun p -> lower.Contains(p))

    static let isScalarType (t: Type) : bool =
        t.IsPrimitive
        || t = typeof<string>
        || t = typeof<Guid>
        || t = typeof<DateTime>
        || t = typeof<DateTimeOffset>
        || t = typeof<TimeSpan>
        || t = typeof<Uri>
        || t.IsEnum
        || (typeof<System.Collections.IEnumerable>.IsAssignableFrom(t) && t <> typeof<string>)

    interface IDestructuringPolicy with
        member _.TryDestructure(value, propertyValueFactory, result) =
            if isNull value then
                false
            else
                let t = value.GetType()
                if isScalarType t then
                    false
                else
                    let props =
                        t.GetProperties(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Instance)
                        |> Array.filter (fun p -> p.CanRead && p.GetIndexParameters().Length = 0)

                    let logProps = ResizeArray<LogEventProperty>()

                    for prop in props do
                        let propName = prop.Name
                        let rawValue = prop.GetValue(value)

                        let logValue : LogEventPropertyValue =
                            if isSensitive propName then
                                ScalarValue("[REDACTED]") :> LogEventPropertyValue
                            elif isNull rawValue then
                                ScalarValue(null) :> LogEventPropertyValue
                            else
                                propertyValueFactory.CreatePropertyValue(rawValue, true)

                        logProps.Add(LogEventProperty(propName, logValue))

                    result <- StructureValue(logProps, t.Name)
                    true
