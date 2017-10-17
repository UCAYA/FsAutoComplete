///Original code from VisualFSharpPowerTools project: https://github.com/fsprojects/VisualFSharpPowerTools/blob/master/src/FSharp.Editing/Common/TypedAstUtils.fs
namespace FsAutoComplete

open System
open System.Text.RegularExpressions
open Microsoft.FSharp.Compiler.SourceCodeServices
open UntypedAstUtils

[<NoComparison>]
type SymbolUse =
    { SymbolUse: FSharpSymbolUse
      IsUsed: bool
      FullNames: Idents[] }

[<AutoOpen>]
module TypedAstUtils =
    let isSymbolLocalForProject (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpParameter -> true
        | :? FSharpMemberOrFunctionOrValue as m -> not m.IsModuleValueOrMember || not m.Accessibility.IsPublic
        | :? FSharpEntity as m -> not m.Accessibility.IsPublic
        | :? FSharpGenericParameter -> true
        | :? FSharpUnionCase as m -> not m.Accessibility.IsPublic
        | :? FSharpField as m -> not m.Accessibility.IsPublic
        | _ -> false

    let isAttribute<'T> (attribute: FSharpAttribute) =
        // CompiledName throws exception on DataContractAttribute generated by SQLProvider
        match Option.attempt (fun _ -> attribute.AttributeType.CompiledName) with
        | Some name when name = typeof<'T>.Name -> true
        | _ -> false

    let hasAttribute<'T> (attributes: seq<FSharpAttribute>) =
        attributes |> Seq.exists isAttribute<'T>

    let tryGetAttribute<'T> (attributes: seq<FSharpAttribute>) =
        attributes |> Seq.tryFind isAttribute<'T>

    let hasModuleSuffixAttribute (entity: FSharpEntity) =
         entity.Attributes
         |> tryGetAttribute<CompilationRepresentationAttribute>
         |> Option.bind (fun a ->
              Option.attempt (fun _ -> a.ConstructorArguments)
              |> Option.bind (fun args -> args |> Seq.tryPick (fun (_, arg) ->
                   let res =
                       match arg with
                       | :? int32 as arg when arg = int CompilationRepresentationFlags.ModuleSuffix ->
                           Some()
                       | :? CompilationRepresentationFlags as arg when arg = CompilationRepresentationFlags.ModuleSuffix ->
                           Some()
                       | _ ->
                           None
                   res)))
         |> Option.isSome

    let isOperator (name: string) =
        name.StartsWith "( " && name.EndsWith " )" && name.Length > 4
            && name.Substring (2, name.Length - 4)
               |> String.forall (fun c -> c <> ' ' && not (Char.IsLetter c))

    let private UnnamedUnionFieldRegex = Regex("^Item(\d+)?$", RegexOptions.Compiled)
    let isUnnamedUnionCaseField (field: FSharpField) = UnnamedUnionFieldRegex.IsMatch(field.Name)

[<AutoOpen>]
module TypedAstExtensionHelpers =
    type FSharpEntity with
        member x.TryGetFullName() =
            Option.attempt (fun _ -> x.TryFullName)
            |> Option.flatten
            |> Option.orTry (fun _ ->
                Option.attempt (fun _ -> String.Join(".", x.AccessPath, x.DisplayName)))

        member x.TryGetFullDisplayName() =
            let fullName = x.TryGetFullName() |> Option.map (fun fullName -> fullName.Split '.')
            let res =
                match fullName with
                | Some fullName ->
                    match Option.attempt (fun _ -> x.DisplayName) with
                    | Some shortDisplayName when not (shortDisplayName.Contains ".") ->
                        Some (fullName |> Array.replace (fullName.Length - 1) shortDisplayName)
                    | _ -> Some fullName
                | None -> None
                |> Option.map (fun fullDisplayName -> String.Join (".", fullDisplayName))
            //debug "GetFullDisplayName: FullName = %A, Result = %A" fullName res
            res

        member x.TryGetFullCompiledName() =
            let fullName = x.TryGetFullName() |> Option.map (fun fullName -> fullName.Split '.')
            let res =
                match fullName with
                | Some fullName ->
                    match Option.attempt (fun _ -> x.CompiledName) with
                    | Some shortCompiledName when not (shortCompiledName.Contains ".") ->
                        Some (fullName |> Array.replace (fullName.Length - 1) shortCompiledName)
                    | _ -> Some fullName
                | None -> None
                |> Option.map (fun fullDisplayName -> String.Join (".", fullDisplayName))
            //debug "GetFullCompiledName: FullName = %A, Result = %A" fullName res
            res

        member x.PublicNestedEntities =
            x.NestedEntities |> Seq.filter (fun entity -> entity.Accessibility.IsPublic)

        member x.TryGetMembersFunctionsAndValues =
            Option.attempt (fun _ -> x.MembersFunctionsAndValues) |> Option.getOrElse ([||] :> _)

    type FSharpMemberOrFunctionOrValue with
        // FullType may raise exceptions (see https://github.com/fsharp/fsharp/issues/307).
        member x.FullTypeSafe = Option.attempt (fun _ -> x.FullType)

        member x.TryGetFullDisplayName() =
            let fullName = Option.attempt (fun _ -> x.FullName.Split '.')
            match fullName with
            | Some fullName ->
                match Option.attempt (fun _ -> x.DisplayName) with
                | Some shortDisplayName when not (shortDisplayName.Contains ".") ->
                    Some (fullName |> Array.replace (fullName.Length - 1) shortDisplayName)
                | _ -> Some fullName
            | None -> None
            |> Option.map (fun fullDisplayName -> String.Join (".", fullDisplayName))

        member x.TryGetFullCompiledOperatorNameIdents() : Idents option =
            // For operator ++ displayName is ( ++ ) compiledName is op_PlusPlus
            if isOperator x.DisplayName && x.DisplayName <> x.CompiledName then
                x.EnclosingEntity
                |> Option.bind (fun e -> e.TryGetFullName())
                |> Option.map (fun enclosingEntityFullName ->
                     Array.append (enclosingEntityFullName.Split '.') [| x.CompiledName |])
            else None

    type FSharpAssemblySignature with
        member x.TryGetEntities() = try x.Entities :> _ seq with _ -> Seq.empty

[<AutoOpen>]
module TypedAstPatterns =

    let private attributeSuffixLength = "Attribute".Length

    let (|Entity|_|) (symbol : FSharpSymbolUse) : (FSharpEntity * (* cleanFullNames *) string list) option =
        match symbol.Symbol with
        | :? FSharpEntity as ent ->
            // strip generic parameters count suffix (List`1 => List)
            let cleanFullName =
                // `TryFullName` for type aliases is always `None`, so we have to make one by our own
                if ent.IsFSharpAbbreviation then
                    [ent.AccessPath + "." + ent.DisplayName]
                else
                    ent.TryFullName
                    |> Option.toList
                    |> List.map (fun fullName ->
                        if ent.GenericParameters.Count > 0 && fullName.Length > 2 then
                            fullName.[0..fullName.Length - 3]
                        else fullName)

            let cleanFullNames =
                cleanFullName
                |> List.collect (fun cleanFullName ->
                    if ent.IsAttributeType then
                        [cleanFullName; cleanFullName.[0..cleanFullName.Length - attributeSuffixLength - 1]]
                    else [cleanFullName]
                    )
            Some (ent, cleanFullNames)
        | _ -> None

    let (|AbbreviatedType|_|) (entity: FSharpEntity) =
        if entity.IsFSharpAbbreviation then Some entity.AbbreviatedType
        else None

    let (|TypeWithDefinition|_|) (ty: FSharpType) =
        if ty.HasTypeDefinition then Some ty.TypeDefinition
        else None

    let rec getEntityAbbreviatedType (entity: FSharpEntity) =
        if entity.IsFSharpAbbreviation then
            match entity.AbbreviatedType with
            | TypeWithDefinition def -> getEntityAbbreviatedType def
            | abbreviatedType -> entity, Some abbreviatedType
        else entity, None

    let rec getAbbreviatedType (fsharpType: FSharpType) =
        if fsharpType.IsAbbreviation then
            getAbbreviatedType fsharpType.AbbreviatedType
        else fsharpType

    let (|Attribute|_|) (entity: FSharpEntity) =
        let isAttribute (entity: FSharpEntity) =
            let getBaseType (entity: FSharpEntity) =
                try
                    match entity.BaseType with
                    | Some (TypeWithDefinition def) -> Some def
                    | _ -> None
                with _ -> None

            let rec isAttributeType (ty: FSharpEntity option) =
                match ty with
                | None -> false
                | Some ty ->
                    match ty.TryGetFullName() with
                    | None -> false
                    | Some fullName ->
                        fullName = "System.Attribute" || isAttributeType (getBaseType ty)
            isAttributeType (Some entity)
        if isAttribute entity then Some() else None

    let (|ValueType|_|) (e: FSharpEntity) =
        if e.IsEnum || e.IsValueType || hasAttribute<MeasureAnnotatedAbbreviationAttribute> e.Attributes then Some()
        else None

    let (|Class|_|) (original: FSharpEntity, abbreviated: FSharpEntity, _) =
        if abbreviated.IsClass
           && (not abbreviated.IsStaticInstantiation || original.IsFSharpAbbreviation) then Some()
        else None

    let (|Record|_|) (e: FSharpEntity) = if e.IsFSharpRecord then Some() else None
    let (|UnionType|_|) (e: FSharpEntity) = if e.IsFSharpUnion then Some() else None
    let (|Delegate|_|) (e: FSharpEntity) = if e.IsDelegate then Some() else None
    let (|FSharpException|_|) (e: FSharpEntity) = if e.IsFSharpExceptionDeclaration then Some() else None
    let (|Interface|_|) (e: FSharpEntity) = if e.IsInterface then Some() else None
    let (|AbstractClass|_|) (e: FSharpEntity) =
        if hasAttribute<AbstractClassAttribute> e.Attributes then Some() else None

    let (|FSharpType|_|) (e: FSharpEntity) =
        if e.IsDelegate || e.IsFSharpExceptionDeclaration || e.IsFSharpRecord || e.IsFSharpUnion
            || e.IsInterface || e.IsMeasure
            || (e.IsFSharp && e.IsOpaque && not e.IsFSharpModule && not e.IsNamespace) then Some()
        else None

    let (|ProvidedType|_|) (e: FSharpEntity) =
        if (e.IsProvided || e.IsProvidedAndErased || e.IsProvidedAndGenerated) && e.CompiledName = e.DisplayName then
            Some()
        else None

    let (|ByRef|_|) (e: FSharpEntity) = if e.IsByRef then Some() else None
    let (|Array|_|) (e: FSharpEntity) = if e.IsArrayType then Some() else None
    let (|FSharpModule|_|) (entity: FSharpEntity) = if entity.IsFSharpModule then Some() else None

    let (|Namespace|_|) (entity: FSharpEntity) = if entity.IsNamespace then Some() else None
    let (|ProvidedAndErasedType|_|) (entity: FSharpEntity) = if entity.IsProvidedAndErased then Some() else None
    let (|Enum|_|) (entity: FSharpEntity) = if entity.IsEnum then Some() else None

    let (|Tuple|_|) (ty: FSharpType option) =
        ty |> Option.bind (fun ty -> if ty.IsTupleType then Some() else None)

    let (|RefCell|_|) (ty: FSharpType) =
        match getAbbreviatedType ty with
        | TypeWithDefinition def when
            def.IsFSharpRecord && def.FullName = "Microsoft.FSharp.Core.FSharpRef`1" -> Some()
        | _ -> None

    let (|FunctionType|_|) (ty: FSharpType) =
        if ty.IsFunctionType then Some()
        else None

    let (|Pattern|_|) (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpUnionCase
        | :? FSharpActivePatternCase -> Some()
        | _ -> None

    /// Field (field, fieldAbbreviatedType)
    let (|Field|_|) (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpField as field -> Some (field, getAbbreviatedType field.FieldType)
        | _ -> None

    let (|MutableVar|_|) (symbol: FSharpSymbol) =
        let isMutable =
            match symbol with
            | :? FSharpField as field -> field.IsMutable && not field.IsLiteral
            | :? FSharpMemberOrFunctionOrValue as func -> func.IsMutable
            | _ -> false
        if isMutable then Some() else None

    /// Entity (originalEntity, abbreviatedEntity, abbreviatedType)
    let (|FSharpEntity|_|) (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpEntity as entity ->
            let abbreviatedEntity, abbreviatedType = getEntityAbbreviatedType entity
            Some (entity, abbreviatedEntity, abbreviatedType)
        | _ -> None

    let (|Parameter|_|) (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpParameter -> Some()
        | _ -> None

    let (|UnionCase|_|) (e: FSharpSymbol) =
        match e with
        | :? FSharpUnionCase as uc -> Some uc
        | _ -> None

    let (|RecordField|_|) (e: FSharpSymbol) =
        match e with
        | :? FSharpField as field ->
            if field.DeclaringEntity.IsFSharpRecord then Some field else None
        | _ -> None

    let (|ActivePatternCase|_|) (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpActivePatternCase as case -> Some case
        | _ -> None

    /// Func (memberFunctionOrValue, fullType)
    let (|MemberFunctionOrValue|_|) (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpMemberOrFunctionOrValue as func -> Some func
        | _ -> None

    /// Constructor (enclosingEntity)
    let (|Constructor|_|) (func: FSharpMemberOrFunctionOrValue) =
        match func.CompiledName with
        | ".ctor" | ".cctor" -> Some func.EnclosingEntity
        | _ -> None

    let (|Function|_|) excluded (func: FSharpMemberOrFunctionOrValue) =
        match func.FullTypeSafe |> Option.map getAbbreviatedType with
        | Some typ when typ.IsFunctionType
                       && not func.IsPropertyGetterMethod
                       && not func.IsPropertySetterMethod
                       && not excluded
                       && not (isOperator func.DisplayName) -> Some()
        | _ -> None

    let (|ExtensionMember|_|) (func: FSharpMemberOrFunctionOrValue) =
        if func.IsExtensionMember then Some() else None

    let (|Event|_|) (func: FSharpMemberOrFunctionOrValue) =
        if func.IsEvent then Some () else None

module UnusedDeclarations =
    open System.Collections.Generic

    let symbolUseComparer =
        { new IEqualityComparer<FSharpSymbolUse> with
              member __.Equals (x, y) = x.Symbol.IsEffectivelySameAs y.Symbol
              member __.GetHashCode x = x.Symbol.GetHashCode() }

    let getSingleDeclarations (symbolsUses: SymbolUse[]): FSharpSymbol[] =
        let symbols = Dictionary<FSharpSymbolUse, int>(symbolUseComparer)

        for symbolUse in symbolsUses do
            match symbols.TryGetValue symbolUse.SymbolUse with
            | true, count -> symbols.[symbolUse.SymbolUse] <- count + 1
            | _ -> symbols.[symbolUse.SymbolUse] <- 1

        symbols
        |> Seq.choose (fun (KeyValue(symbolUse, count)) ->
            match symbolUse.Symbol with
            | UnionCase _ when isSymbolLocalForProject symbolUse.Symbol -> Some symbolUse.Symbol
            // Determining that a record, DU or module is used anywhere requires
            // inspecting all their enclosed entities (fields, cases and func / vals)
            // for usefulness, which is too expensive to do. Hence we never gray them out.
            | FSharpEntity ((Record | UnionType | Interface | FSharpModule), _, _ | Class) -> None
            // FCS returns inconsistent results for override members; we're skipping these symbols.
            | MemberFunctionOrValue func when func.IsOverrideOrExplicitInterfaceImplementation -> None
            // Usage of DU case parameters does not give any meaningful feedback; we never gray them out.
            | Parameter -> None
            | _ when count = 1 && symbolUse.IsFromDefinition && isSymbolLocalForProject symbolUse.Symbol ->
                    Some symbolUse.Symbol
                | _ -> None)
        |> Seq.toArray