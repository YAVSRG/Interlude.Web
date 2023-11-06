﻿namespace Interlude.Web.Server.Bot

open System
open Discord
open Discord.WebSocket
open Prelude.Backbeat.Archive
open Interlude.Web.Server.Domain

module UserCommands =

    let user_by_name (name: string) =
        if Seq.forall (fun (c: char) -> Seq.contains c Users.VALID_USERNAME_CHARACTERS) name then
            User.by_username name
        else
            None

    // Requires user to exist but no particular permissions
    let dispatch
        (client: DiscordSocketClient)
        (userId: int64, userInfo: User)
        (context: SocketMessage)
        (command: string)
        (args: string list)
        =
        task {
            let reply msg =
                task {
                    let! _ = context.Channel.SendMessageAsync(msg)
                    return ()
                }

            let reply_embed embed =
                task {
                    let! _ = context.Channel.SendMessageAsync(embed = embed)
                    return ()
                }

            let reply_emoji emoji =
                task {
                    let! _ = context.AddReactionAsync(Emoji.Parse emoji)
                    return ()
                }

            match command with
            | "s"
            | "search" ->
                match args with

                | [] ->
                    do! reply "Enter a search term, for example: $search PLANET//SHAPER artist:Camellia creator:Evening"

                | query :: _ ->
                    let matches = Backbeat.Charts.search query |> List.ofSeq

                    match matches with
                    | [] -> do! reply "No matches found."
                    | (song, charts) :: [] ->
                        let embed =
                            EmbedBuilder(Title = song.Title)
                                .AddField(
                                    (if song.Artists.Length > 1 then "Artists" else "Artist"),
                                    String.concat ", " song.Artists,
                                    true
                                )

                        if song.OtherArtists <> [] then
                            embed.AddField("Featuring", String.concat ", " song.OtherArtists, true)
                            |> ignore

                        if song.Remixers <> [] then
                            embed.AddField("Remixed by", String.concat ", " song.Remixers, true) |> ignore

                        if song.Tags <> [] then
                            embed.AddField("Tags", String.concat ", " song.Tags) |> ignore

                        if song.Source <> None then
                            embed.AddField("Source", song.Source.Value) |> ignore

                        embed.WithColor(Color.Blue) |> ignore

                        for chart in charts do
                            embed.AddField(
                                chart.DifficultyName + " by " + String.concat ", " chart.Creators,
                                String.concat "  |  " (chart.Sources |> List.map Backbeat.Charts.format_source)
                            )
                            |> ignore

                        do! reply_embed (embed.Build())
                    | _ ->
                        let embed =
                            EmbedBuilder(
                                Title =
                                    match matches.Length with
                                    | 30 -> "30+ matches found"
                                    | i -> sprintf "%i matches found" i
                            )
                                .WithDescription(
                                    String.concat
                                        "\n"
                                        (List.map
                                            (fun (song: Song, charts) -> song.FormattedTitle.Replace("*", "\\*"))
                                            matches)
                                )
                                .WithColor(Color.Blue)

                        do! reply_embed (embed.Build())
            | "fl"
            | "friends"
            | "friendlist" ->
                let friends = Friends.friends_list (userId)

                let embed =
                    EmbedBuilder(Title = sprintf "%s's friends list" userInfo.Username)
                        .WithColor(Color.Blue)
                        .WithDescription(
                            if friends.Length = 0 then
                                "nobody :("
                            else
                                friends |> Array.map (fun user -> user.Username) |> String.concat "\n"
                        )

                do! reply_embed (embed.Build())
            | "f"
            | "friend" ->
                match args with
                | [] -> do! reply "Enter a username, for example: $friend Percyqaz"
                | username :: _ ->
                    match user_by_name username with
                    | Some(id, _) ->
                        Friends.add_friend (userId, id)
                        do! reply_emoji ":white_check_mark:"
                    | None -> do! reply "No user found."
            | "uf"
            | "unfriend" ->
                match args with
                | [] -> do! reply "Enter a username, for example: $unfriend Percyqaz"
                | username :: _ ->
                    match user_by_name username with
                    | Some(id, _) ->
                        Friends.remove_friend (userId, id)
                        do! reply_emoji ":white_check_mark:"
                    | None -> do! reply "No user found."
            | "pc"
            | "profilecolor" ->
                match args with
                | [] -> do! reply "Enter a badge name, for example $profilecolor early-tester"
                | badge :: [] ->
                    if userInfo.Badges.Contains badge then
                        let color = (Badge.badge_color badge).[0]
                        User.update_color (userId, color)
                        do! reply_emoji ":white_check_mark:"
                    else
                        do! reply "You don't have this badge."
                | badge :: choice :: _ ->
                    if userInfo.Badges.Contains badge then
                        let colors = Badge.badge_color badge

                        match System.Int32.TryParse(choice) with
                        | true, c when c > 0 && c <= colors.Length ->
                            User.update_color (userId, colors.[c - 1])

                            do! reply_emoji ":white_check_mark:"
                        | _ -> do! reply (sprintf "The options for this badge are 1-%i" colors.Length)
                    else
                        do! reply "You don't have this badge."
            | "p"
            | "profile" ->
                let profile (userId, userInfo) =
                    task {
                        let now = DateTimeOffset.UtcNow

                        let format_time_ago (ts: int64) =
                            let ts: TimeSpan = now - DateTimeOffset.FromUnixTimeMilliseconds(ts)

                            if ts.TotalDays > 365.0 then
                                sprintf "%.0fy ago" (ts.TotalDays / 365.0)
                            elif ts.TotalDays > 30.0 then
                                sprintf "%.0fmo ago" (ts.TotalDays / 30.0)
                            elif ts.TotalDays > 7.0 then
                                sprintf "%.0fw ago" (ts.TotalDays / 7.0)
                            elif ts.TotalDays > 1.0 then
                                sprintf "%.0fd ago" ts.TotalDays
                            elif ts.TotalHours > 1.0 then
                                sprintf "%.0fh ago" ts.TotalHours
                            elif ts.TotalMinutes > 5.0 then
                                sprintf "%.0fm ago" ts.TotalMinutes
                            else
                                "Just now"

                        let format_mods (score: Score) =
                            if score.Mods.IsEmpty then
                                sprintf "%.2fx" score.Rate
                            else
                                sprintf "%.2fx*" score.Rate

                        let recent_scores = Score.get_recent userId

                        let embed =
                            let color =
                                userInfo.Color
                                |> Option.defaultValue Badge.DEFAULT_COLOR
                                |> Drawing.Color.FromArgb
                                |> Color.op_Explicit

                            let embed =
                                EmbedBuilder(
                                    Title = userInfo.Username,
                                    Footer =
                                        EmbedFooterBuilder(
                                            Text = (String.concat ", " userInfo.Badges).Replace("-", " ").ToUpper()
                                        )
                                )
                                    .WithColor(color)

                            if recent_scores.Length > 0 then
                                embed.WithFields(
                                    EmbedFieldBuilder(Name = "Recent scores", IsInline = true)
                                        .WithValue(
                                            recent_scores
                                            |> Array.map (fun s ->
                                                match Backbeat.Charts.by_hash s.ChartId with
                                                | Some(_, song) -> song.Title
                                                | None -> "???" |> sprintf "`%-20s`"
                                            )
                                            |> String.concat "\n"
                                        ),
                                    EmbedFieldBuilder(Name = "..", IsInline = true)
                                        .WithValue(
                                            recent_scores
                                            |> Array.map (fun s ->
                                                sprintf
                                                    "`%6.2f%%` `%6s` `%6s` `%8s`"
                                                    (s.Score * 100.0)
                                                    (Backbeat.rulesets.[s.RulesetId].LampName s.Lamp)
                                                    (format_mods s)
                                                    (format_time_ago s.Timestamp)
                                            )
                                            |> String.concat "\n"
                                        )
                                )
                            else
                                embed

                        do! reply_embed (embed.Build())
                    }

                match args with
                | [] -> do! profile (userId, userInfo)
                | name :: _ ->
                    match user_by_name name with
                    | Some(id, user) -> do! profile (id, user)
                    | None -> do! reply "No user found."

            | "help" -> do! reply "Available commands: $search, $friends, $friend, $unfriend, $profilecolor, $profile"

            | _ -> ()
        }
