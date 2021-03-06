﻿namespace FSpotify

open System

type SubscriptionLevel = |Premium |Free |Open

type PrivateUser = {
    birthdate: string option
    country: Country option
    display_name: string
    email: string option
    followers: Followers
    href: Uri
    id: SpotifyId
    images: Image list
    product: SubscriptionLevel option
    ``type``: string
    uri: SpotifyUri
}

module User =
    
    let request = Request.createFromEndpoint Request.Get "users"

    let me = 
        Request.createFromEndpoint Request.Get "me"
        |> Request.parse<PrivateUser,_>

    let user (SpotifyId id) =
        request
        |> Request.withUrlPath id
        |> Request.parse<PublicUser,_>