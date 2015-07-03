﻿namespace FSpotify

open System
open System.Net
open System.IO
open System.Web
open System.Reflection
open Microsoft.FSharp.Reflection
open Serializing

exception SpotifyError of string*string

module Request =

    type HttpVerb = Get | Post | Put | Delete

    type HttpHeader =
    | ContentType
    | Custom of string

    type ResponseBuilder<'a> = string -> 'a

    type Parameter = QueryParameter of string*string

    type Request<'a,'b> = {
        verb: HttpVerb
        path: string
        parameters: Parameter list
        headers: Map<HttpHeader,string>
        body: string
        responseMapper: ResponseBuilder<'a>
        optionals: 'b
    }

    let withUrl (url: Uri) (request: Request<'a,'b>) =
        let path = url.GetLeftPart(UriPartial.Path)
        let parameters =
            if url.Query.StartsWith "?" then
                url.Query.Substring(1).Split('&')
                |> Array.map (fun str -> str.Split('='))
                |> Array.map (Array.map Uri.UnescapeDataString)
                |> Array.map (fun keyValue -> QueryParameter(keyValue.[0],keyValue.[1]))
                |> Array.toList
                |> List.append request.parameters
            else
                request.parameters

        {request with path = path; parameters = parameters}

    let withVerb verb request ={request with verb = verb}

    type ResultOption<'a> = Success of 'a | Error of string*string

    let mapResponse<'a,'b,'c> (fn: 'a -> 'c) (request: Request<'a,'b>) =       
        {
            verb = request.verb
            headers = request.headers
            body = request.body
            path = request.path
            parameters = request.parameters
            responseMapper = request.responseMapper >> fn
            optionals = request.optionals
        }

    let withOptionals (fn: 'b -> 'c) (request: Request<'a,'b>)=
        {
               verb = request.verb
               headers = request.headers
               path = request.path
               body = request.body
               parameters = request.parameters
               responseMapper = request.responseMapper
               optionals = request.optionals |> fn
        }

    let unwrap<'a> = mapResponse<string,'a,string> << Serializing.unwrap

    let parse<'a,'b> = mapResponse<string,'b,'a> Serializing.deserialize<'a>

    let mapPath fn request = {request with path = fn request.path}

    let withUrlPath path (request: Request<'a,'b>) = {request with path = sprintf "%s/%s" request.path path}

    let withQueryParameter (name,value) request = {request with parameters = QueryParameter(name,value) :: request.parameters}

    let withBody body request = {request with body = body}

    let withHeader (name,value) request = {request with headers = request.headers |> Map.add name value}

    let withFormBody args = 
        let args =
            args  
            |> List.map (fun (key,value) ->
                sprintf "%s=%s" (Uri.EscapeDataString(key)) (Uri.EscapeDataString(value))
            )
            |> String.concat "&"
        
        withHeader (ContentType, "application/x-www-form-urlencoded") >> withBody args

    let withJsonBody data =
        withHeader (ContentType, "application/json") >> withBody (Serializing.serialize data)

    let withAuthorization {access_token = token; token_type = token_type} =
        withHeader (Custom "Authorization",sprintf "%s %s" token_type token)

    [<AbstractClass>]
    type OptionalParameter<'a> () =
        inherit Attribute()
        abstract member Apply: 'a -> Parameter

    type OptionalQueryParameter<'a>(name,mapper) =
        inherit OptionalParameter<'a> ()
        override this.Apply value = QueryParameter(name,mapper value)

    let send ({ path = path
                parameters = parameters
                responseMapper = responseMapper
                verb = verb
                body = body
                headers = headers
                optionals = optionals}) =

        let parameters = 
            if (box optionals) <> null && FSharpType.IsRecord(optionals.GetType()) then
                optionals.GetType()
                |> FSharpType.GetRecordFields
                |> Array.toList
                |> List.choose (fun property ->
                    let attributes =
                        property.GetCustomAttributes()
                    let attribute =
                        attributes
                        |> Seq.find (fun attribute ->

                            let rec isGenericSubclass (genericBase: Type) (someType: Type) =
                                if someType.IsGenericType && someType.GetGenericTypeDefinition() = genericBase then
                                    true
                                elif someType.BaseType <> null then
                                    isGenericSubclass genericBase someType.BaseType
                                else
                                    false
                            isGenericSubclass typedefof<OptionalParameter<_>> (attribute.GetType())
                        )
                    let value = FSharpValue.GetRecordField(optionals,property)
                    if value <> null then
                        let _,fields =
                            FSharpValue.GetUnionFields(value,value.GetType())
                        Some <| (attribute.GetType().GetMethod("Apply").Invoke(attribute,[|fields.[0]|]) :?> Parameter)
                    else
                        None
                )
            else
                []
            |> List.append parameters
        
        let queryString =
            parameters
            |> List.choose (function
                | QueryParameter (key,value) -> Some(key,value)
            )
            |> List.map (fun (key,value) ->
                sprintf "%s=%s" (Uri.EscapeDataString key) (Uri.EscapeDataString value)
            )
            |> String.concat "&"
            |> fun queryString -> if queryString <> "" then "?" + queryString else  "" 
        try

            let httpRequest = WebRequest.Create(path + queryString)

            httpRequest.Method <-
                match verb with
                | Get -> "GET"
                | Post -> "POST"
                | Put -> "PUT"
                | Delete -> "DELETE"

            headers
            |> Map.toList
            |> List.iter (fun (name,value) ->
                match name with
                | ContentType ->
                    httpRequest.ContentType <- value
                | Custom name ->
                    httpRequest.Headers.Add(name,value)
            )

            if body <> "" then
                use requestStream = httpRequest.GetRequestStream()
                use requestWriter = new StreamWriter(requestStream)
                requestWriter.Write(body)
                requestWriter.Flush()
                requestWriter.Close()

            use httpResponse = httpRequest.GetResponse()
            use stream = httpResponse.GetResponseStream()
            use streamReader = new StreamReader(stream)

            streamReader.ReadToEnd() |> responseMapper
        with
        | :? WebException as error ->
            let responseStream = error.Response.GetResponseStream()
            let errorBody = (new StreamReader(responseStream)).ReadToEnd()
            let error =
                if errorBody <> "" then
                    (Serializing.unwrap "error" errorBody) |> deserialize<ErrorObject>
                else
                    {status = (error.Response :?> HttpWebResponse).StatusCode
                                |> LanguagePrimitives.EnumToValue
                                |> string
                     message = error.Message}
            printfn "Debug: Spotify returned an error (%s: %s)" error.status error.message
            raise <| SpotifyError(error.status,error.message)

    let trySend operation =
        try
            send operation |> Success
        with
        | :? SpotifyError as error ->
            // Todo: Somehow pattern match the tuple above?!?!
            Error (error.Data0,error.Data1)

    let asyncSend operation = async {
        return send operation
    }

    let asyncTrySend operation = async {
        return trySend operation
    }

    let create verb url = 
        {
            verb = verb
            path = ""
            headers = Map.empty
            body = ""
            parameters = []
            responseMapper = string
            optionals = ()
        }
        |> withUrl url

    let createFromEndpoint verb endpoint = create verb (Uri "https://api.spotify.com/v1") |> withUrlPath endpoint


