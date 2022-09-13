#if USE_SEARCH_EXTENSION_API
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Search;
using Object = UnityEngine.Object;

namespace UnityEditor.Search.Providers
{
    delegate double FilterDataHandler<in T>(ImageData data, T param);
    delegate T FilterParamHandler<out T>(string param);

    class EdgeInfo
    {
        public EdgeHistogram histogram;
        public double[] densities;
    }

    enum ImageEngineFilterType
    {
        Unary,
        Binary
    }

    struct ImageEngineFilter
    {
        public string filterId;
        public string param;
        public string op;
        public double similitude;
        public ImageEngineFilterType type;
    }

    struct ImageEngineFilterData
    {
        public ImageEngineFilter engineFilter;
        public string filterId => engineFilter.filterId;
        public string filterName;
        public string description;
        public Delegate filterDataFunc;
        public Delegate filterParamFunc;

        public Type filterType { get; private set; }

        public static ImageEngineFilterData Create<T>(string filterId, string filterName, string description,
            FilterDataHandler<T> filterDataFunc, FilterParamHandler<T> filterParamFunc,
            string defaultReplacementParam = "Assets", string defaultReplacementOp = ">",
            double defaultReplacementSimilitude = 0.75, ImageEngineFilterType filterType = ImageEngineFilterType.Binary)
        {
            var engineFilter = new ImageEngineFilter()
            {
                filterId = filterId,
                param = defaultReplacementParam,
                op = defaultReplacementOp,
                similitude = defaultReplacementSimilitude,
                type = filterType
            };
            return Create<T>(engineFilter, filterName, description, filterDataFunc, filterParamFunc);
        }

        public static ImageEngineFilterData Create<T>(ImageEngineFilter engineFilter, string filterName, string description,
            FilterDataHandler<T> filterDataFunc, FilterParamHandler<T> filterParamFunc)
        {
            return new ImageEngineFilterData()
            {
                engineFilter = engineFilter,
                filterName = filterName,
                description = description,
                filterDataFunc = filterDataFunc,
                filterParamFunc = filterParamFunc,
                filterType = typeof(T)
            };
        }

        public Func<ImageData, T, double> GetFilterDataHandler<T>()
        {
            if (filterType != typeof(T))
                return null;
            return Delegate.CreateDelegate(typeof(Func<ImageData, T, double>), filterDataFunc.Target, filterDataFunc.Method) as Func<ImageData, T, double>;
        }

        public Func<string, T> GetFilterParamHandler<T>()
        {
            if (filterType != typeof(T))
                return null;
            return Delegate.CreateDelegate(typeof(Func<string, T>), filterParamFunc.Target, filterParamFunc.Method) as Func<string, T>;
        }
    }

    class ImageProvider
    {
        const string k_ProviderId = "img";
        const string k_FilterId = "img:";
        const string k_DisplayName = "Image";

        static List<ImageDatabase> m_ImageIndexes;

        static List<ImageEngineFilterData> s_ImageFiltersData;
        static QueryEngine<ImageData> s_QueryEngine;
        static Dictionary<string, SearchProvider> s_GroupProviders;

        static List<ImageDatabase> indexes
        {
            get
            {
                if (m_ImageIndexes == null)
                {
                    UpdateImageIndexes();
                }

                return m_ImageIndexes;
            }
        }

        [UsedImplicitly, SearchItemProvider]
        internal static SearchProvider CreateProvider()
        {
            return new SearchProvider(k_ProviderId, k_DisplayName)
            {
                filterId = k_FilterId,
                priority = 1,
                showDetails = true,
                showDetailsOptions = ShowDetailsOptions.Inspector | ShowDetailsOptions.Default,
                fetchItems = (context, items, provider) => SearchItems(context, provider),
                toObject = (item, type) => GetItemObject(item),
                isExplicitProvider = true,
                fetchDescription = FetchDescription,
                fetchThumbnail = (item, context) => SearchUtils.GetAssetThumbnailFromPath(item.id),
                fetchPreview = (item, context, size, options) => SearchUtils.GetAssetPreviewFromPath(item.id, options),
                // trackSelection = (item, context) => TrackSelection(item),
                // fetchKeywords = FetchKeywords,
                // startDrag = (item, context) => DragItem(item, context),
                onEnable = OnEnable,
                fetchPropositions = FetchPropositions,
            };
        }

        static Object GetItemObject(SearchItem item)
        {
            return AssetDatabase.LoadAssetAtPath(item.id, typeof(Texture2D));
        }

        [SearchActionsProvider]
        internal static IEnumerable<SearchAction> CreateSearchActions()
        {
            yield return new SearchAction(k_ProviderId, "similitude", null, "Get similar images")
            {
                enabled = items => items.Count == 1 && IsTexture(items.First()),
                handler = OpenSimilarImageSearch
            };
            yield return new SearchAction("asset", "similitude", null, "Get similar images")
            {
                enabled = items => items.Count == 1 && IsTexture(items.First()),
                handler = OpenSimilarImageSearch
            };
        }

        static bool IsTexture(SearchItem item)
        {
            var texture2d = item.ToObject<Texture2D>();
            return texture2d != null;
        }

        static void OpenSimilarImageSearch(SearchItem obj)
        {
            if (s_ImageFiltersData == null)
                return;

            var texture2d = obj.ToObject<Texture2D>();
            var path = AssetDatabase.GetAssetPath(texture2d);
            var sanitizedPath = SanitizePath(path);

            var filters = s_ImageFiltersData.Where(d => d.engineFilter.type == ImageEngineFilterType.Binary).Select(d =>
            {
                var newFilter = d.engineFilter;
                newFilter.param = sanitizedPath;
                return newFilter;
            });

            var query = BuildQueryForImage(filters);

            var context = SearchService.CreateContext(k_ProviderId, query);
            var viewState = new SearchViewState(context, SearchViewFlags.GridView | SearchViewFlags.OpenInBuilderMode);
            viewState.group = k_ProviderId;
            SearchService.ShowWindow(viewState);
        }

        static string BuildQueryForImage(IEnumerable<ImageEngineFilter> filters)
        {
            var sb = new StringBuilder();
            sb.Append(k_FilterId);

            foreach (var imageEngineFilter in filters)
            {
                sb.Append(" ").Append(BuildFilter(imageEngineFilter));
            }

            return sb.ToString();
        }

        static ImageEngineFilterData GetImageEngineFilterData(string filterId)
        {
            if (s_ImageFiltersData == null)
                return default;
            return s_ImageFiltersData.FirstOrDefault(data => data.filterId == filterId);
        }

        static string FetchDescription(SearchItem item, SearchContext context)
        {
            if (item.options.HasFlag(SearchItemOptions.FullDescription) && !item.options.HasFlag(SearchItemOptions.Compacted))
            {
                var imageData = (ImageData)item.data;
                var sb = new StringBuilder();
                sb.AppendLine($"Best Color: {imageData.bestColors[0]}");
                sb.AppendLine($"Edge densities: ({string.Join(", ", imageData.edgeDensities)})");
                sb.AppendLine("Edge Histogram:");
                sb.Append($"{imageData.edgeHistogram}");
                sb.AppendLine($"Geometric moments: ({string.Join(", ", imageData.geometricMoments)})");
                return sb.ToString();
            }
            else
            {
                return null;
            }
        }

        static void OnEnable()
        {
            if (s_ImageFiltersData == null)
            {
                s_ImageFiltersData = new List<ImageEngineFilterData>()
                {
                    ImageEngineFilterData.Create("color", "Color", "Find images that match a certain color.",
                        GetColorSimilitude, GetColorFromParameter,
                        "blue", filterType: ImageEngineFilterType.Unary),
                    ImageEngineFilterData.Create("hist", "Histogram", "Find images that are close to another image's color distribution.",
                        GetHistogramSimilitude, GetHistogramFromParameter),
                    ImageEngineFilterData.Create("edge", "Edge", "Find images that are close to another image's edges orientation.",
                        GetEdgeHistogramSimilitude, GetEdgeHistogramFromParameter),
                    ImageEngineFilterData.Create("density", "Density", "Find images that are close to another image's edges density.",
                        GetEdgeDensitySimilitude, GetEdgeHistogramFromParameter),
                    ImageEngineFilterData.Create("geom", "Geometry", "Find images that are close to another image's geometric moments.",
                        GetGeometricMomentSimilitude, GetGeometricMomentFromParameter)
                };
            }

            if (s_GroupProviders == null)
            {
                s_GroupProviders = new Dictionary<string, SearchProvider>();
                var currentProvider = SearchService.GetProvider(k_ProviderId);
                var i = 2;
                foreach (var imageEngineFilterData in s_ImageFiltersData)
                {
                    var provider = SearchUtils.CreateGroupProvider(currentProvider, imageEngineFilterData.filterName, i++, true);
                    s_GroupProviders.Add(imageEngineFilterData.filterId, provider);
                }
            }

            if (s_QueryEngine == null)
            {
                s_QueryEngine = new QueryEngine<ImageData>();

                foreach (var imageEngineFilterData in s_ImageFiltersData)
                {
                    AddEngineFilter(imageEngineFilterData);
                }
                s_QueryEngine.SetSearchDataCallback(DefaultSearchDataCallback);
            }
        }

        static void AddEngineFilter(ImageEngineFilterData imageEngineFilterData)
        {
            var type = imageEngineFilterData.filterType;

            var thisClassType = typeof(ImageProvider);
            var method = thisClassType.GetMethod("AddEngineFilterTyped", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                Debug.LogError("Method AddEngineFilterTyped was not found");
                return;
            }
            var typedMethod = method.MakeGenericMethod(type);
            typedMethod.Invoke(null, new object[] { imageEngineFilterData });
        }

        static void AddEngineFilterTyped<T>(ImageEngineFilterData imageEngineFilterData)
        {
            if (s_QueryEngine == null)
                return;
            if (imageEngineFilterData.GetFilterDataHandler<T>() is not { } getFilterDataHandler)
                return;
            if (imageEngineFilterData.GetFilterParamHandler<T>() is not { } getFilterParamHandler)
                return;
            s_QueryEngine.AddFilter(imageEngineFilterData.filterId, getFilterDataHandler, getFilterParamHandler);
        }

        static IEnumerable<SearchProposition> FetchPropositions(SearchContext arg1, SearchPropositionOptions arg2)
        {
            var category = "Image Similitude";
            if (s_ImageFiltersData == null)
                yield break;
            foreach (var imageEngineFilterData in s_ImageFiltersData)
            {
                yield return new SearchProposition(category, imageEngineFilterData.filterName, BuildFilter(imageEngineFilterData.engineFilter), imageEngineFilterData.description);
            }
        }

        static string BuildFilter(string filterId, string parameter, string op, double similitude)
        {
            return BuildFilter(filterId, parameter, op, similitude.ToString());
        }

        static string BuildFilter(string filterId, string parameter, string op, string similitude)
        {
            return $"{filterId}({parameter}){op}{similitude}";
        }

        static string BuildFilter(ImageEngineFilter filter)
        {
            return BuildFilter(filter.filterId, filter.param, filter.op, filter.similitude);
        }

        static double GetColorSimilitude(ImageData imageData, Color paramColor)
        {
            var similarities = imageData.bestShades.Select(colorInfo =>
            {
                var color32 = ImageUtils.IntToColor32(colorInfo.color);
                var color = ImageUtils.Color32ToColor(color32);
                var ratio = colorInfo.ratio;
                return ImageUtils.WeightedSimilarity(color, ratio, paramColor);
            });
            return similarities.Sum();
        }

        static Color GetColorFromParameter(string param)
        {
            if (param.StartsWith("#"))
            {
                ColorUtility.TryParseHtmlString(Convert.ToString(param), out var color);
                return color;
            }

            switch (param)
            {
                case "red": return Color.red;
                case "green": return Color.green;
                case "blue": return Color.blue;
                case "black": return Color.black;
                case "white": return Color.white;
                case "yellow": return Color.yellow;
            }

            return Color.black;
        }

        static double GetHistogramSimilitude(ImageData imageData, Histogram context)
        {
            if (context == null)
                return 0.0;
            return 1.0 - ImageUtils.HistogramDistance(imageData.histogram, context, HistogramDistance.MDPA);
        }

        static double GetEdgeHistogramSimilitude(ImageData imageData, EdgeInfo context)
        {
            if (context == null)
                return 0.0;

            return 1.0 - ImageUtils.HistogramDistance(imageData.edgeHistogram, context.histogram, HistogramDistance.MDPA);
        }

        static double GetEdgeDensitySimilitude(ImageData imageData, EdgeInfo context)
        {
            if (context == null)
                return 0.0;

            var densitySimilitude = 1.0 - MathUtils.NormalizedDistance(imageData.edgeDensities, context.densities);
            return densitySimilitude;
        }

        static Histogram GetHistogramFromParameter(string param)
        {
            var sanitizedParam = SanitizePath(param);
            var idb = indexes.FirstOrDefault(db => db.ContainsAsset(sanitizedParam));
            if (idb != null)
            {
                var imageData = idb.GetImageDataFromPath(sanitizedParam);
                return imageData.histogram;
            }

            if (!File.Exists(sanitizedParam))
                return null;

            var texture = AssetDatabase.LoadMainAssetAtPath(sanitizedParam) as Texture2D;
            if (texture == null)
                return null;

            Color32[] pixels;
            if (texture.isReadable)
                pixels = texture.GetPixels32();
            else
            {
                var copy = TextureUtils.CopyTextureReadable(texture, texture.width, texture.height);
                pixels = copy.GetPixels32();
            }
            var histogram = new Histogram();
            ImageUtils.ComputeHistogram(pixels, histogram);
            return histogram;
        }

        static EdgeInfo GetEdgeHistogramFromParameter(string param)
        {
            var sanitizedParam = SanitizePath(param);
            var idb = indexes.FirstOrDefault(db => db.ContainsAsset(sanitizedParam));
            if (idb != null)
            {
                var imageData = idb.GetImageDataFromPath(sanitizedParam);
                return new EdgeInfo() { histogram = imageData.edgeHistogram, densities = imageData.edgeDensities };
            }

            if (!File.Exists(sanitizedParam))
                return null;

            var texture = AssetDatabase.LoadMainAssetAtPath(sanitizedParam) as Texture2D;
            if (texture == null)
                return null;

            Color[] pixels = TextureUtils.GetPixels(texture);
            var histogram = new EdgeHistogram();
            var densities = new double[histogram.channels];
            ImageUtils.ComputeEdgesHistogramAndDensity(pixels, texture.width, texture.height, histogram, densities);
            return new EdgeInfo() { histogram = histogram, densities = densities };
        }

        static double GetGeometricMomentSimilitude(ImageData imageData, double[] geoMoments)
        {
            if (geoMoments == null)
                return 0.0;

            // This is incorrect. The geometric moments are not values that are bound between 0..1
            var geometricSimilitude = 1.0 - MathUtils.NormalizedDistance(imageData.geometricMoments, geoMoments);
            return geometricSimilitude;
        }

        static double[] GetGeometricMomentFromParameter(string param)
        {
            var sanitizedParam = SanitizePath(param);
            var idb = indexes.FirstOrDefault(db => db.ContainsAsset(sanitizedParam));
            if (idb != null)
            {
                var imageData = idb.GetImageDataFromPath(sanitizedParam);
                return imageData.geometricMoments;
            }

            if (!File.Exists(sanitizedParam))
                return null;

            var texture = AssetDatabase.LoadMainAssetAtPath(sanitizedParam) as Texture2D;
            if (texture == null)
                return null;

            var pixels = TextureUtils.GetPixels(texture);
            var geoMoments = new double[3];
            ImageUtils.ComputeSecondOrderInvariant(pixels, texture.width, texture.height, geoMoments);
            return geoMoments;
        }

        static string[] DefaultSearchDataCallback(ImageData data)
        {
            var assetPath = indexes.Select(db => db.GetAssetPath(data.guid)).FirstOrDefault(path => path != null);
            return new[] { assetPath };
        }

        static void UpdateImageIndexes()
        {
            m_ImageIndexes = ImageDatabase.Enumerate().ToList();
        }

        static IEnumerator SearchItems(SearchContext context, SearchProvider provider)
        {
            var searchQuery = context.searchQuery;

            if (searchQuery.Length > 0)
                yield return indexes.Select(db => SearchIndexes(searchQuery, context, provider, db));
        }

        static IEnumerator SearchIndexes(string searchQuery, SearchContext context, SearchProvider provider, ImageDatabase db)
        {
            var query = s_QueryEngine.ParseQuery(searchQuery);

            var filterNodes = GetFilterNodes(query.evaluationGraph);

            // Get all modifiers
            var scoreModifiers = new Dictionary<string, Func<ImageData, int, ImageDatabase, int>>();
            foreach (var filterNode in filterNodes)
            {
                var engineFilterData = GetImageEngineFilterData(filterNode.filterId);
                var scoreModifier = GetScoreModifier(engineFilterData, filterNode.paramValue);
                scoreModifiers.Add(filterNode.filterId, scoreModifier);
            }

            // Find all similar images matching all filters
            foreach (var item in FilterImages(query, scoreModifiers.Values, context, provider, db))
                yield return item;

            // Then find all similar images for each filter
            foreach (var filterNode in filterNodes)
            {
                var scoreModifier = scoreModifiers[filterNode.filterId];
                var filterQuery = BuildFilter(filterNode.filterId, filterNode.paramValue, filterNode.operatorId, filterNode.filterValue);
                var subQuery = s_QueryEngine.ParseQuery(filterQuery);
                var groupProvider = s_GroupProviders[filterNode.filterId];

                foreach (var item in FilterImages(subQuery, new[] {scoreModifier}, context, groupProvider, db))
                    yield return item;
            }
        }

        static IEnumerable<SearchItem> FilterImages(ParsedQuery<ImageData> query, IEnumerable<Func<ImageData, int, ImageDatabase, int>> scoreModifiers, SearchContext context, SearchProvider provider, ImageDatabase db)
        {
            var filteredImageData = query.Apply(db.imagesData);
            foreach (var data in filteredImageData)
            {
                var score = 0;
                foreach (var scoreModifier in scoreModifiers)
                {
                    score = scoreModifier(data, score, db);
                }

                var assetPath = db.GetAssetPath(data.guid);
                var name = Path.GetFileNameWithoutExtension(assetPath);

                yield return provider.CreateItem(context, assetPath, score, name, null, null, data);
            }
        }

        static IEnumerable<IFilterNode> GetFilterNodes(QueryGraph graph)
        {
            return EnumerateNodes(graph).Where(node => node.type == QueryNodeType.Filter).Select(node => node as IFilterNode);
        }

        static Func<ImageData, int, ImageDatabase, int> GetScoreModifier(ImageEngineFilterData engineFilterData, string paramValue)
        {
            var filterType = engineFilterData.filterType;

            var thisClassType = typeof(ImageProvider);
            var method = thisClassType.GetMethod("GetScoreModifierTyped", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new Exception("Method GetScoreModifierTyped was not found");
            var typedMethod = method.MakeGenericMethod(filterType);
            return typedMethod.Invoke(null, new object[] { engineFilterData, paramValue }) as Func<ImageData, int, ImageDatabase, int>;
        }

        static Func<ImageData, int, ImageDatabase, int> GetScoreModifierTyped<TParam>(ImageEngineFilterData engineFilterData, string paramValue)
        {
            var dataHandler = engineFilterData.GetFilterDataHandler<TParam>();
            var paramHandler = engineFilterData.GetFilterParamHandler<TParam>();
            var resolvedParamValue = paramHandler(paramValue);

            return (imageData, inputScore, db) =>
            {
                var similitude = dataHandler(imageData, resolvedParamValue);
                return inputScore - (int)(similitude * 100);
            };
        }

        internal static IEnumerable<IQueryNode> EnumerateNodes(QueryGraph graph)
        {
            if (graph.empty)
                yield break;

            var nodeStack = new Stack<IQueryNode>();
            nodeStack.Push(graph.root);

            while (nodeStack.Count > 0)
            {
                var currentNode = nodeStack.Pop();
                if (!currentNode.leaf && currentNode.children != null)
                {
                    foreach (var child in currentNode.children)
                    {
                        nodeStack.Push(child);
                    }
                }

                yield return currentNode;
            }
        }

        static string SanitizePath(string path)
        {
            return path.Replace("\\", "/");
        }
    }
}
#endif