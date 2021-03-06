﻿using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PopcornApi.Attributes;
using PopcornApi.Database;
using PopcornApi.Models.Cast;
using PopcornApi.Models.Movie;
using PopcornApi.Models.Torrent.Movie;
using PopcornApi.Services.Caching;
using PopcornApi.Services.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using PopcornApi.Extensions;
using PopcornApi.Helpers;
using Utf8Json.Resolvers;
using JsonSerializer = Utf8Json.JsonSerializer;

namespace PopcornApi.Controllers
{
    [Route("api/[controller]")]
    public class MoviesController : Controller
    {
        /// <summary>
        /// The logging service
        /// </summary>
        private readonly ILoggingService _loggingService;

        /// <summary>
        /// The caching service
        /// </summary>
        private readonly ICachingService _cachingService;

        /// <summary>
        /// Movies
        /// </summary>
        /// <param name="loggingService">The logging service</param>
        /// <param name="cachingService">The caching service</param>
        public MoviesController(ILoggingService loggingService, ICachingService cachingService)
        {
            _loggingService = loggingService;
            _cachingService = cachingService;
        }

        // GET api/movies
        [HttpGet]
        public async Task<IActionResult> Get([RequiredFromQuery] int page, [FromQuery] int limit,
            [FromQuery] int minimum_rating, [FromQuery] string query_term,
            [FromQuery] string genre, [FromQuery] string sort_by)
        {
            var watch = new Stopwatch();
            watch.Start();
            var nbMoviesPerPage = 20;
            if (limit >= 20 && limit <= 50)
                nbMoviesPerPage = limit;

            var currentPage = 1;
            if (page >= 1)
            {
                currentPage = page;
            }

            var queryTerm = string.Empty;
            if (!string.IsNullOrWhiteSpace(query_term))
            {
                queryTerm = query_term;
            }

            var genreFilter = string.Empty;
            if (!string.IsNullOrWhiteSpace(genre))
            {
                genreFilter = genre;
            }

            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $@"type=movies&page={page}&limit={limit}&minimum_rating={minimum_rating}&query_term={
                            query_term
                        }&genre={genre}&sort_by={sort_by}"));
            try
            {
                var cachedMovies = await _cachingService.GetCache(hash);
                if (cachedMovies != null)
                {
                    try
                    {
                        watch.Stop();
                        return Content(cachedMovies, "application/json");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var skipParameter = new SqlParameter("@skip", (currentPage - 1) * nbMoviesPerPage);
                var takeParameter = new SqlParameter("@take", nbMoviesPerPage);
                var ratingParameter = new SqlParameter("@rating", minimum_rating);
                var queryParameter = new SqlParameter("@Keywords", string.Format(@"""{0}""", queryTerm));
                var genreParameter = new SqlParameter("@genre", genreFilter);
                var query = @"
                    SELECT DISTINCT
                        Movie.Title, Movie.Year, Movie.Rating, Movie.PosterImage, Movie.ImdbCode, Movie.GenreNames, Torrent.Peers, Torrent.Seeds, COUNT(*) OVER () as TotalCount, Movie.DateUploadedUnix, Movie.Id, Movie.DownloadCount, Movie.LikeCount
                    FROM 
                        MovieSet AS Movie
                    CROSS APPLY
					(
						SELECT TOP 1 Torrent.MovieId, Torrent.Peers, Torrent.Seeds FROM TorrentMovieSet AS Torrent
						WHERE Torrent.MovieId = Movie.Id  AND Torrent.Url <> '' AND Torrent.Url IS NOT NULL
					) Torrent

                    INNER JOIN
                        CastSet AS Cast
                    ON Cast.MovieId = Movie.Id
                    WHERE 1 = 1";

                if (minimum_rating > 0 && minimum_rating < 10)
                {
                    query += @" AND
                        Rating >= @rating";
                }

                if (!string.IsNullOrWhiteSpace(query_term))
                {
                    query += @" AND
                        (CONTAINS(Movie.Title, @Keywords) OR CONTAINS(Cast.Name, @Keywords) OR CONTAINS(Movie.ImdbCode, @Keywords) OR CONTAINS(Cast.ImdbCode, @Keywords))";
                }

                if (!string.IsNullOrWhiteSpace(genre))
                {
                    query += @" AND
                        CONTAINS(Movie.GenreNames, @genre)";
                }

                query += " GROUP BY Movie.Id, Movie.Title, Movie.Year, Movie.Rating, Movie.PosterImage, Movie.ImdbCode, Movie.GenreNames, Torrent.Peers, Torrent.Seeds, Movie.DateUploadedUnix, Movie.Id, Movie.DownloadCount, Movie.LikeCount";

                if (!string.IsNullOrWhiteSpace(sort_by))
                {
                    switch (sort_by)
                    {
                        case "title":
                            query += " ORDER BY Movie.Title ASC";
                            break;
                        case "year":
                            query += " ORDER BY Movie.Year DESC";
                            break;
                        case "rating":
                            query += " ORDER BY Movie.Rating DESC";
                            break;
                        case "peers":
                            query += " ORDER BY Torrent.Peers DESC";
                            break;
                        case "seeds":
                            query += " ORDER BY Torrent.Seeds DESC";
                            break;
                        case "download_count":
                            query += " ORDER BY Movie.DownloadCount DESC";
                            break;
                        case "like_count":
                            query += " ORDER BY Movie.LikeCount DESC";
                            break;
                        case "date_added":
                            query += " ORDER BY Movie.DateUploadedUnix DESC";
                            break;
                        default:
                            query += " ORDER BY Movie.DateUploadedUnix DESC";
                            break;
                    }
                }
                else
                {
                    query += " ORDER BY Movie.DateUploadedUnix DESC";
                }

                query += @" OFFSET @skip ROWS 
                    FETCH NEXT @take ROWS ONLY";

                var moviesQuery = await context.Database.ExecuteSqlQueryAsync(query, new CancellationToken(),
                    skipParameter, takeParameter,
                    ratingParameter, queryParameter,
                    genreParameter);
                var reader = moviesQuery.DbDataReader;
                var count = 0;
                var movies = new List<MovieLightJson>();
                while (await reader.ReadAsync())
                {
                    var movie = new MovieLightJson
                    {
                        Title = !await reader.IsDBNullAsync(0) ? reader.GetString(0) : string.Empty,
                        Year = !await reader.IsDBNullAsync(1) ? reader.GetInt32(1) : 0,
                        Rating = !await reader.IsDBNullAsync(2) ? reader.GetDouble(2) : 0d,
                        PosterImage = !await reader.IsDBNullAsync(3) ? reader.GetString(3) : string.Empty,
                        ImdbCode = !await reader.IsDBNullAsync(4) ? reader.GetString(4) : string.Empty,
                        Genres = !await reader.IsDBNullAsync(5) ? reader.GetString(5) : string.Empty
                    };
                    movies.Add(movie);
                    count = !await reader.IsDBNullAsync(8) ? reader.GetInt32(8) : 0;
                }

                var response = new MovieLightResponse
                {
                    TotalMovies = count,
                    Movies = movies
                };

                var json = JsonSerializer.ToJsonString(response, StandardResolver.SnakeCase);
                await _cachingService.SetCache(hash, json, TimeSpan.FromDays(1));
                watch.Stop();
                return Content(json, "application/json");
            }
        }

        // GET api/movies/ids
        [HttpPost]
        [Route("ids")]
        public async Task<IActionResult> GetMoviesByIds([FromBody] IEnumerable<string> imdbIds)
        {
            if (!imdbIds.Any())
            {
                return Json(new MovieLightResponse
                {
                    Movies = new List<MovieLightJson>(),
                    TotalMovies = 0
                });
            }

            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $@"type=movies&imdbIds={string.Join(',', imdbIds)}"));
            try
            {
                var cachedMovies = await _cachingService.GetCache(hash);
                if (cachedMovies != null)
                {
                    try
                    {
                        return Content(cachedMovies, "application/json");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var query = @"
                    SELECT DISTINCT
                        Movie.Title, Movie.Year, Movie.Rating, Movie.PosterImage, Movie.ImdbCode, Movie.GenreNames, COUNT(*) OVER () as TotalCount
                    FROM 
                        MovieSet AS Movie
                    WHERE
                        Movie.ImdbCode IN ({@imdbIds})
                    ORDER BY Movie.Rating DESC";

                using (var cmd = new SqlCommand(query,
                    new SqlConnection(context.Database.GetDbConnection().ConnectionString)))
                {
                    cmd.AddArrayParameters(imdbIds, "@imdbIds");
                    await cmd.Connection.OpenAsync();
                    var reader = await cmd.ExecuteReaderAsync(new CancellationToken());
                    var count = 0;
                    var movies = new List<MovieLightJson>();
                    while (await reader.ReadAsync())
                    {
                        var movie = new MovieLightJson
                        {
                            Title = !await reader.IsDBNullAsync(0) ? reader.GetString(0) : string.Empty,
                            Year = !await reader.IsDBNullAsync(1) ? reader.GetInt32(1) : 0,
                            Rating = !await reader.IsDBNullAsync(2) ? reader.GetDouble(2) : 0d,
                            PosterImage = !await reader.IsDBNullAsync(3) ? reader.GetString(3) : string.Empty,
                            ImdbCode = !await reader.IsDBNullAsync(4) ? reader.GetString(4) : string.Empty,
                            Genres = !await reader.IsDBNullAsync(5) ? reader.GetString(5) : string.Empty
                        };
                        movies.Add(movie);
                        count = !await reader.IsDBNullAsync(6) ? reader.GetInt32(6) : 0;
                    }

                    var response = new MovieLightResponse
                    {
                        TotalMovies = count,
                        Movies = movies
                    };

                    var json = JsonSerializer.ToJsonString(response, StandardResolver.SnakeCase);
                    await _cachingService.SetCache(hash, json, TimeSpan.FromDays(1));
                    return Content(json, "application/json");
                }
            }
        }

        // GET api/movies/similar
        [HttpPost]
        [Route("similar")]
        public async Task<IActionResult> GetSimilar([FromBody] IEnumerable<string> imdbIds, [RequiredFromQuery] int page, [FromQuery] int limit,
            [FromQuery] int minimum_rating, [FromQuery] string query_term,
            [FromQuery] string genre, [FromQuery] string sort_by)
        {
            var nbMoviesPerPage = 20;
            if (limit >= 20 && limit <= 50)
                nbMoviesPerPage = limit;

            var currentPage = 1;
            if (page >= 1)
            {
                currentPage = page;
            }

            var queryTerm = string.Empty;
            if (!string.IsNullOrWhiteSpace(query_term))
            {
                queryTerm = query_term;
            }

            var genreFilter = string.Empty;
            if (!string.IsNullOrWhiteSpace(genre))
            {
                genreFilter = genre;
            }

            if (!imdbIds.Any())
            {
                return Json(new MovieLightResponse
                {
                    Movies = new List<MovieLightJson>(),
                    TotalMovies = 0
                });
            }

            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $@"type=movies&similar&imdbIds={string.Join(',', imdbIds)}&page={page}&limit={limit}&minimum_rating={minimum_rating}&query_term={
                            query_term
                        }&genre={genre}&sort_by={sort_by}"));

            try
            {
                var cachedMovies = await _cachingService.GetCache(hash);
                if (cachedMovies != null)
                {
                    try
                    {
                        return Content(cachedMovies, "application/json");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var skipParameter = new SqlParameter("@skip", (currentPage - 1) * nbMoviesPerPage);
                var takeParameter = new SqlParameter("@take", nbMoviesPerPage);
                var ratingParameter = new SqlParameter("@rating", minimum_rating);
                var queryParameter = new SqlParameter("@Keywords", string.Format(@"""{0}""", queryTerm));
                var genreParameter = new SqlParameter("@genre", genreFilter);
                var query = @"
                    SELECT DISTINCT
                        Movie.Title, Movie.Year, Movie.Rating, Movie.PosterImage, Movie.ImdbCode, Movie.GenreNames, COUNT(*) OVER () as TotalCount
                    FROM 
                        MovieSet AS Movie
                    WHERE
                        Movie.ImdbCode IN (SELECT 
                            Similar.TmdbId                      
                        FROM 
                            Similar AS Similar
                        INNER JOIN
					    (
						    SELECT Movie.ID
						    FROM 
							    MovieSet AS Movie
						    WHERE 
							    Movie.ImdbCode IN ({@imdbIds})
						) Movie
					ON Similar.MovieId = Movie.Id)
                    AND 1 = 1";

                if (minimum_rating > 0 && minimum_rating < 10)
                {
                    query += @" AND
                        Rating >= @rating";
                }

                if (!string.IsNullOrWhiteSpace(query_term))
                {
                    query += @" AND
                        (CONTAINS(Movie.Title, @Keywords) OR CONTAINS(Movie.ImdbCode, @Keywords))";
                }

                if (!string.IsNullOrWhiteSpace(genre))
                {
                    query += @" AND
                        CONTAINS(Movie.GenreNames, @genre)";
                }

                query += " ORDER BY Movie.Rating DESC";
                query += @" OFFSET @skip ROWS 
                    FETCH NEXT @take ROWS ONLY";

                using (var cmd = new SqlCommand(query,
                    new SqlConnection(context.Database.GetDbConnection().ConnectionString)))
                {
                    cmd.AddArrayParameters(imdbIds, "@imdbIds");
                    cmd.Parameters.Add(skipParameter);
                    cmd.Parameters.Add(takeParameter);
                    cmd.Parameters.Add(ratingParameter);
                    cmd.Parameters.Add(queryParameter);
                    cmd.Parameters.Add(genreParameter);
                    await cmd.Connection.OpenAsync();
                    var reader = await cmd.ExecuteReaderAsync(new CancellationToken());
                    var count = 0;
                    var movies = new List<MovieLightJson>();
                    while (await reader.ReadAsync())
                    {
                        var movie = new MovieLightJson
                        {
                            Title = !await reader.IsDBNullAsync(0) ? reader.GetString(0) : string.Empty,
                            Year = !await reader.IsDBNullAsync(1) ? reader.GetInt32(1) : 0,
                            Rating = !await reader.IsDBNullAsync(2) ? reader.GetDouble(2) : 0d,
                            PosterImage = !await reader.IsDBNullAsync(3) ? reader.GetString(3) : string.Empty,
                            ImdbCode = !await reader.IsDBNullAsync(4) ? reader.GetString(4) : string.Empty,
                            Genres = !await reader.IsDBNullAsync(5) ? reader.GetString(5) : string.Empty
                        };
                        movies.Add(movie);
                        count = !await reader.IsDBNullAsync(6) ? reader.GetInt32(6) : 0;
                    }

                    var response = new MovieLightResponse
                    {
                        TotalMovies = count,
                        Movies = movies
                    };

                    var json = JsonSerializer.ToJsonString(response, StandardResolver.SnakeCase);
                    await _cachingService.SetCache(hash, json, TimeSpan.FromDays(1));
                    return Content(json, "application/json");
                }
            }
        }

        // GET api/movies/light/tt3640424
        [HttpGet("light/{imdb}")]
        public async Task<IActionResult> GetLight(string imdb)
        {
            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"light:{imdb}"));
            try
            {
                var cachedMovie = await _cachingService.GetCache(hash);
                if (cachedMovie != null)
                {
                    try
                    {
                        return Content(cachedMovie, "application/json");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var imdbParameter = new SqlParameter("@imdbCode", imdb);
                var query = @"
                    SELECT 
                        Movie.Title, Movie.Year, Movie.Rating, Movie.PosterImage, Movie.ImdbCode, Movie.GenreNames
                    FROM 
                        MovieSet AS Movie
                    WHERE
                        Movie.ImdbCode = @imdbCode";
                var movieQuery =
                    await context.Database.ExecuteSqlQueryAsync(query, new CancellationToken(), imdbParameter);
                var reader = movieQuery.DbDataReader;
                var movie = new MovieLightJson();
                while (await reader.ReadAsync())
                {
                    movie.Title = !await reader.IsDBNullAsync(0) ? reader.GetString(0) : string.Empty;
                    movie.Year = !await reader.IsDBNullAsync(1) ? reader.GetInt32(1) : 0;
                    movie.Rating = !await reader.IsDBNullAsync(2) ? reader.GetDouble(2) : 0d;
                    movie.PosterImage = !await reader.IsDBNullAsync(3) ? reader.GetString(3) : string.Empty;
                    movie.ImdbCode = !await reader.IsDBNullAsync(4) ? reader.GetString(4) : string.Empty;
                    movie.Genres = !await reader.IsDBNullAsync(5) ? reader.GetString(5) : string.Empty;
                }

                if (string.IsNullOrEmpty(movie.ImdbCode))
                    return BadRequest();

                var json = JsonSerializer.ToJsonString(movie, StandardResolver.SnakeCase);
                await _cachingService.SetCache(hash, json, TimeSpan.FromDays(1));
                return Content(json, "application/json");
            }
        }

        // GET api/movies/cast/nm0000123
        [HttpGet("cast/{castId}")]
        public async Task<IActionResult> GetFromCast(string castId)
        {
            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"cast:{castId}"));
            try
            {
                var cachedMovie = await _cachingService.GetCache(hash);
                if (cachedMovie != null)
                {
                    try
                    {
                        return Content(cachedMovie, "application/json");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var imdbParameter = new SqlParameter("@imdbCode", castId);
                var query = @"
                    SELECT 
                        Movie.Title, Movie.Year, Movie.Rating, Movie.PosterImage, Movie.ImdbCode, Movie.GenreNames
                    FROM 
                        MovieSet AS Movie
                    INNER JOIN
                        CastSet AS Cast
                    ON 
                        Cast.MovieId = Movie.Id
                    WHERE
                        Cast.ImdbCode = @imdbCode";
                var movieQuery =
                    await context.Database.ExecuteSqlQueryAsync(query, new CancellationToken(), imdbParameter);
                var reader = movieQuery.DbDataReader;
                var movies = new List<MovieLightJson>();
                while (await reader.ReadAsync())
                {
                    var movie = new MovieLightJson
                    {
                        Title = !await reader.IsDBNullAsync(0) ? reader.GetString(0) : string.Empty,
                        Year = !await reader.IsDBNullAsync(1) ? reader.GetInt32(1) : 0,
                        Rating = !await reader.IsDBNullAsync(2) ? reader.GetDouble(2) : 0d,
                        PosterImage = !await reader.IsDBNullAsync(3) ? reader.GetString(3) : string.Empty,
                        ImdbCode = !await reader.IsDBNullAsync(4) ? reader.GetString(4) : string.Empty,
                        Genres = !await reader.IsDBNullAsync(5) ? reader.GetString(5) : string.Empty
                    };
                    movies.Add(movie);
                }

                var response = new MovieLightResponse
                {
                    TotalMovies = movies.Count,
                    Movies = movies
                };

                var json = JsonSerializer.ToJsonString(response, StandardResolver.SnakeCase);
                await _cachingService.SetCache(hash, json, TimeSpan.FromDays(1));
                return Content(json, "application/json");
            }
        }

        // GET api/movies/tt3640424
        [HttpGet("{imdb}")]
        public async Task<IActionResult> Get(string imdb)
        {
            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"full:{imdb}"));
            try
            {
                var cachedMovie = await _cachingService.GetCache(hash);
                if (cachedMovie != null)
                {
                    try
                    {
                        return Content(cachedMovie, "application/json");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var movie =
                    await context.MovieSet.Include(a => a.Torrents)
                        .Include(a => a.Cast)
                        .Include(a => a.Similars)
                        .Include(a => a.Genres).AsQueryable()
                        .FirstOrDefaultAsync(
                            document => document.ImdbCode == imdb);
                if (movie == null) return BadRequest();

                var movieJson = ConvertMovieToJson(movie);
                var json = JsonSerializer.ToJsonString(movieJson, StandardResolver.SnakeCase);
                await _cachingService.SetCache(hash, json, TimeSpan.FromDays(1));
                return Content(json, "application/json");
            }
        }

        /// <summary>
        /// Convert a <see cref="Movie"/> to a <see cref="MovieJson"/>
        /// </summary>
        /// <param name="movie"></param>
        /// <returns></returns>
        private MovieJson ConvertMovieToJson(Movie movie)
        {
            return new MovieJson
            {
                Rating = movie.Rating,
                Torrents = movie.Torrents.Select(torrent => new TorrentMovieJson
                {
                    DateUploadedUnix = torrent.DateUploadedUnix,
                    Peers = torrent.Peers,
                    Seeds = torrent.Seeds,
                    Quality = torrent.Quality,
                    Url = torrent.Url,
                    DateUploaded = torrent.DateUploaded,
                    Hash = torrent.Hash,
                    Size = torrent.Size,
                    SizeBytes = torrent.SizeBytes
                }).ToList(),
                Title = movie.Title,
                DateUploadedUnix = movie.DateUploadedUnix,
                Genres = movie.Genres.Select(genre => genre.Name).ToList(),
                Cast = movie.Cast.Select(cast => new CastJson
                {
                    CharacterName = cast.CharacterName,
                    Name = cast.Name,
                    ImdbCode = cast.ImdbCode,
                    SmallImage = cast.SmallImage
                }).ToList(),
                Runtime = movie.Runtime,
                Url = movie.Url,
                Year = movie.Year,
                Slug = movie.Slug,
                LikeCount = movie.LikeCount,
                DownloadCount = movie.DownloadCount,
                ImdbCode = movie.ImdbCode,
                DateUploaded = movie.DateUploaded,
                BackgroundImage = movie.BackgroundImage,
                DescriptionFull = movie.DescriptionFull,
                DescriptionIntro = movie.DescriptionIntro,
                Language = movie.Language,
                MpaRating = movie.MpaRating,
                PosterImage = movie.PosterImage,
                TitleLong = movie.TitleLong,
                YtTrailerCode = movie.YtTrailerCode,
                Similar = movie.Similars.Select(a => a.TmdbId).ToList()
            };
        }
    }
}